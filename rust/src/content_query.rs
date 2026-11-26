#![allow(dead_code)]

use std::{
    collections::{HashMap, HashSet},
    sync::OnceLock,
};
use chrono::NaiveDate;
use serde::{Deserialize, Serialize};
use crate::{
    content::ContentMatchMode,
    util::{normalize_key, now_ms, words},
};

const HOUR_MS: i64 = 3_600_000;
const DAY_MS: i64 = 86_400_000;
const KB: i64 = 1_000;
const MB: i64 = 1_000_000;
const GB: i64 = 1_000_000_000;
const TB: i64 = 1_000_000_000_000;

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct FacetChip {
    pub kind: String,
    pub label: String,
    pub token: String,
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct QueryFacets {
    pub extensions: Vec<String>,
    pub min_bytes: Option<i64>,
    pub max_bytes: Option<i64>,
    pub modified_after: Option<i64>,
    pub modified_before: Option<i64>,
    pub tags: Vec<String>,
    pub online: Option<bool>,
    pub chips: Vec<FacetChip>,
}

impl QueryFacets {
    pub fn is_empty(&self) -> bool {
        self.extensions.is_empty()
            && self.min_bytes.is_none()
            && self.max_bytes.is_none()
            && self.modified_after.is_none()
            && self.modified_before.is_none()
            && self.tags.is_empty()
            && self.online.is_none()
    }

    pub fn has_filters(&self) -> bool {
        !self.is_empty()
    }

    fn add_extensions(&mut self, exts: &[&str], label: String, token: &str) {
        for ext in exts {
            let normalized = normalize_key(ext.trim_start_matches('.'));
            if !normalized.is_empty() && !self.extensions.contains(&normalized) {
                self.extensions.push(normalized);
            }
        }
        self.chips.push(FacetChip {
            kind: "ext".into(),
            label,
            token: token.into(),
        });
    }

    fn set_min_bytes(&mut self, bytes: i64, label: String, token: &str) {
        self.min_bytes = Some(self.min_bytes.map_or(bytes, |prev| prev.max(bytes)));
        self.chips.push(FacetChip {
            kind: "size".into(),
            label,
            token: token.into(),
        });
    }

    fn set_max_bytes(&mut self, bytes: i64, label: String, token: &str) {
        self.max_bytes = Some(self.max_bytes.map_or(bytes, |prev| prev.min(bytes)));
        self.chips.push(FacetChip {
            kind: "size".into(),
            label,
            token: token.into(),
        });
    }

    fn set_modified_after(&mut self, ts: i64, label: String, token: &str) {
        self.modified_after = Some(self.modified_after.map_or(ts, |prev| prev.max(ts)));
        self.chips.push(FacetChip {
            kind: "date".into(),
            label,
            token: token.into(),
        });
    }

    fn set_modified_before(&mut self, ts: i64, label: String, token: &str) {
        self.modified_before = Some(self.modified_before.map_or(ts, |prev| prev.min(ts)));
        self.chips.push(FacetChip {
            kind: "date".into(),
            label,
            token: token.into(),
        });
    }

    fn set_modified_range(&mut self, after: i64, before: i64, label: String, token: &str) {
        self.modified_after = Some(self.modified_after.map_or(after, |prev| prev.max(after)));
        self.modified_before = Some(self.modified_before.map_or(before, |prev| prev.min(before)));
        self.chips.push(FacetChip {
            kind: "date".into(),
            label,
            token: token.into(),
        });
    }

    fn add_tag(&mut self, tag: &str, token: &str) {
        let normalized = normalize_key(tag);
        if normalized.is_empty() {
            return;
        }
        if !self.tags.contains(&normalized) {
            self.tags.push(normalized.clone());
        }
        self.chips.push(FacetChip {
            kind: "tag".into(),
            label: format!("Tag: #{normalized}"),
            token: token.into(),
        });
    }

    fn set_online(&mut self, online: bool, token: &str) {
        self.online = Some(online);
        self.chips.push(FacetChip {
            kind: "status".into(),
            label: if online { "Status: Online".into() } else { "Status: Offline".into() },
            token: token.into(),
        });
    }
}

#[derive(Clone, Debug, Eq, PartialEq, Hash)]
pub struct Term {
    pub text: String,
    pub prefix: bool,
    pub phrase: bool,
    pub verbatim: bool,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum Node {
    Term(Term),
    Not(Box<Node>),
    Join(Box<Node>, Box<Node>, bool),
}

impl Node {
    pub fn join(self, right: Self, any: bool) -> Self {
        Self::Join(Box::new(self), Box::new(right), any)
    }

    pub fn positive<'a>(&'a self, excluded: bool, terms: &mut Vec<&'a Term>) {
        match self {
            Self::Term(term) if !excluded && !terms.contains(&term) => terms.push(term),
            Self::Term(_) => {}
            Self::Not(node) => node.positive(!excluded, terms),
            Self::Join(a, b, _) => {
                a.positive(excluded, terms);
                b.positive(excluded, terms);
            }
        }
    }

    pub fn compile(&self, corrections: &HashMap<String, Vec<String>>, excluded: bool) -> (String, bool) {
        match self {
            Self::Term(term) => (term.render(corrections, excluded), false),
            Self::Not(node) => {
                let (expression, complement) = node.compile(corrections, !excluded);
                (expression, !complement)
            }
            Self::Join(a, b, any) => {
                let (mut a, ac) = a.compile(corrections, excluded);
                let (mut b, bc) = b.compile(corrections, excluded);
                if ac == bc {
                    let op = if *any != ac { "OR" } else { "AND" };
                    (format!("({a} {op} {b})"), ac)
                } else {
                    if ac != *any {
                        std::mem::swap(&mut a, &mut b);
                    }
                    (format!("({a} NOT {b})"), *any)
                }
            }
        }
    }
}

impl Term {
    pub fn render(&self, corrections: &HashMap<String, Vec<String>>, excluded: bool) -> String {
        let exact = quote(&self.text, self.prefix);
        if excluded || self.prefix || self.verbatim || corrections.is_empty() {
            return exact;
        }
        if !self.phrase {
            return corrections
                .get(&self.text.to_lowercase())
                .map_or_else(
                    || exact.clone(),
                    |alternatives| {
                        format!(
                            "({exact} OR {})",
                            alternatives
                                .iter()
                                .map(|word| quote(word, true))
                                .collect::<Vec<_>>()
                                .join(" OR ")
                        )
                    },
                );
        }
        let mut phrases = vec![String::new()];
        let mut corrected = false;
        for word in words(&self.text) {
            let mut choices = vec![quote(word, false)];
            if let Some(alternatives) = corrections.get(&word.to_lowercase()) {
                corrected = true;
                choices.extend(alternatives.iter().map(|word| quote(word, true)));
            }
            phrases = phrases
                .iter()
                .flat_map(|phrase| {
                    choices.iter().map(move |choice| {
                        if phrase.is_empty() {
                            choice.clone()
                        } else {
                            format!("{phrase} + {choice}")
                        }
                    })
                })
                .take(32)
                .collect();
        }
        if corrected {
            format!("({exact} OR {})", phrases.join(" OR "))
        } else {
            exact
        }
    }
}

pub fn quote(word: &str, prefix: bool) -> String {
    format!(
        "\"{}\"{}",
        word.replace('"', "\"\""),
        if prefix { "*" } else { "" }
    )
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SmartQuery {
    pub groups: Vec<Node>,
    pub excluded: Vec<Node>,
    pub inferred: Vec<String>,
    pub facets: QueryFacets,
    pub cleaned_query: String,
    pub expanded_concepts: Vec<String>,
}

impl SmartQuery {
    pub fn root(&self) -> Node {
        let include = self
            .groups
            .iter()
            .cloned()
            .reduce(|a, b| a.join(b, true))
            .unwrap();
        self.excluded.iter().cloned().fold(include, |root, exclusion| {
            root.join(Node::Not(Box::new(exclusion)), false)
        })
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ParsedContentQuery {
    pub root: Option<Node>,
    pub explicit: bool,
    pub natural: Option<SmartQuery>,
    pub cleaned_query: String,
    pub facets: QueryFacets,
    pub expanded_concepts: Vec<String>,
}

pub fn parse_query(input: &str, mode: ContentMatchMode) -> Option<ParsedContentQuery> {
    parse_with_now(input, mode, now_ms())
}

pub fn parse_with_now(
    input: &str,
    mode: ContentMatchMode,
    now_ms: i64,
) -> Option<ParsedContentQuery> {
    if input.encode_utf16().count() > 4096 {
        return None;
    }
    if mode == ContentMatchMode::ExactPhrase {
        let (cleaned_query, facets) = extract_explicit_facets_only(input, now_ms);
        let text = words(&cleaned_query).collect::<Vec<_>>().join(" ");
        if text.is_empty() {
            return (!facets.is_empty()).then_some(ParsedContentQuery {
                root: None,
                explicit: false,
                natural: None,
                cleaned_query: String::new(),
                facets,
                expanded_concepts: Vec::new(),
            });
        }
        let root = Node::Term(Term {
            text,
            prefix: false,
            phrase: true,
            verbatim: false,
        });
        return Some(ParsedContentQuery {
            root: Some(root),
            explicit: false,
            natural: None,
            cleaned_query,
            facets,
            expanded_concepts: Vec::new(),
        });
    }

    let (cleaned_query, facets) = extract_facets(input, now_ms);
    if cleaned_query.trim().is_empty() {
        return (!facets.is_empty()).then_some(ParsedContentQuery {
            root: None,
            explicit: false,
            natural: None,
            cleaned_query,
            facets,
            expanded_concepts: Vec::new(),
        });
    }

    if mode == ContentMatchMode::Smart {
        match smart_from_cleaned(&cleaned_query, facets.clone()) {
            Some(Some(natural)) => {
                let root = Some(natural.root());
                let expanded_concepts = natural.expanded_concepts.clone();
                return Some(ParsedContentQuery {
                    root,
                    explicit: false,
                    natural: Some(natural),
                    cleaned_query,
                    facets,
                    expanded_concepts,
                });
            }
            Some(None) => return None,
            None => {}
        }
    }

    let (root, explicit) = parse_boolean(&cleaned_query, mode)?;
    Some(ParsedContentQuery {
        root: Some(root),
        explicit,
        natural: None,
        cleaned_query,
        facets,
        expanded_concepts: Vec::new(),
    })
}

pub fn parse(input: &str, mode: ContentMatchMode) -> Option<(Node, bool)> {
    if input.encode_utf16().count() > 4096 {
        return None;
    }
    if mode == ContentMatchMode::ExactPhrase {
        let (cleaned, _) = extract_explicit_facets_only(input, now_ms());
        let text = words(&cleaned).collect::<Vec<_>>().join(" ");
        return (!text.is_empty()).then_some((
            Node::Term(Term {
                text,
                prefix: false,
                phrase: true,
                verbatim: false,
            }),
            false,
        ));
    }
    let (cleaned, _) = extract_facets(input, now_ms());
    parse_boolean(&cleaned, mode)
}

fn parse_boolean(input: &str, mode: ContentMatchMode) -> Option<(Node, bool)> {
    static TOKENS: OnceLock<regex::Regex> = OnceLock::new();
    let re = TOKENS.get_or_init(|| {
        regex::Regex::new(r#""[^"]*"|"|&&|\|\|?|[()-]|[\p{L}\p{N}][\p{L}\p{M}\p{N}]*\*?"#)
            .unwrap()
    });
    let tokens: Vec<_> = re
        .find_iter(input)
        .filter(|m| {
            m.as_str() != "-"
                || !input[..m.start()]
                    .chars()
                    .next_back()
                    .is_some_and(|c| c.is_alphanumeric())
        })
        .map(|m| m.as_str())
        .take(97)
        .collect();
    if tokens.is_empty()
        || tokens.len() > 96
        || tokens.iter().filter(|t| words(t).next().is_some()).count() > 32
    {
        return None;
    }
    let mut parser = Parser {
        tokens,
        index: 0,
        explicit: false,
        any: mode == ContentMatchMode::AnyWord,
    };
    let root = parser.or(0)?;
    (parser.index == parser.tokens.len()).then_some((root, parser.explicit))
}

const STOP_WORDS: &str = "a an the and or of to in on for with about from by at as is are was were be been that this these those it its my your our their file files document documents looking find search show me please something where what when how into onto over under between through during before after above below up down out off all any both each few more most other some such only own same so than too very can will just should now de het een van voor met over naar uit dat die dit mijn bestand bestanden zoek zoeken vind geef toon als ook graag en of ik je";

// None delegates to the Boolean parser. Some(None) is an invalid natural query.
pub fn smart(input: &str) -> Option<Option<SmartQuery>> {
    smart_with_now(input, now_ms())
}

pub fn smart_with_now(input: &str, now_ms: i64) -> Option<Option<SmartQuery>> {
    if input.encode_utf16().count() > 4096 {
        return Some(None);
    }
    let (cleaned, facets) = extract_facets(input, now_ms);
    smart_from_cleaned(&cleaned, facets)
}

fn smart_from_cleaned(cleaned: &str, facets: QueryFacets) -> Option<Option<SmartQuery>> {
    static TOKENS: OnceLock<regex::Regex> = OnceLock::new();
    let re = TOKENS.get_or_init(|| {
        regex::Regex::new(r#""[^"]*"|"|&&|\|\|?|[()/\-]|[\p{L}\p{N}][\p{L}\p{M}\p{N}]*\*?"#)
            .unwrap()
    });
    let tokens: Vec<_> = re
        .find_iter(cleaned)
        .filter(|m| match m.as_str() {
            "-" => !cleaned[..m.start()]
                .chars()
                .next_back()
                .is_some_and(char::is_alphanumeric),
            "/" => {
                cleaned[..m.start()]
                    .ends_with(|c: char| c.is_alphanumeric() || c == '*' || c == '"')
                    && cleaned[m.end()..].starts_with(|c: char| c.is_alphanumeric() || c == '"')
            }
            _ => true,
        })
        .map(|m| m.as_str())
        .take(97)
        .collect();
    if tokens
        .iter()
        .any(|token| matches!(*token, "AND" | "OR" | "NOT" | "(" | ")" | "&&" | "|" | "||"))
    {
        return None;
    }
    Some(natural_query(&tokens, cleaned, facets))
}

fn is_negation_word(word: &str) -> bool {
    matches!(
        word.to_ascii_lowercase().as_str(),
        "without" | "except" | "excluding" | "exclude" | "not" | "zonder" | "behalve" | "niet"
    )
}

fn is_stop_word(word: &str) -> bool {
    STOP_WORDS
        .split_ascii_whitespace()
        .any(|stop| stop.eq_ignore_ascii_case(word))
}

fn natural_query(
    tokens: &[&str],
    cleaned_query: &str,
    facets: QueryFacets,
) -> Option<SmartQuery> {
    if tokens.len() > 96 || tokens.iter().filter(|t| words(t).next().is_some()).count() > 32 {
        return None;
    }
    let mut iter = tokens.iter().copied().peekable();
    let mut raw_groups: Vec<Node> = Vec::new();
    let mut excluded: Vec<Node> = Vec::new();
    let mut inferred: Vec<String> = Vec::new();
    let mut excluded_words: HashSet<String> = HashSet::new();

    while let Some(token) = iter.next() {
        let inferred_neg = is_negation_word(token)
            && iter.peek().is_some_and(|next| words(next).next().is_some());
        let is_excluded = inferred_neg || token == "-";
        let token = if is_excluded { iter.next()? } else { token };
        let mut group = natural_term(token)?;
        let mut alternatives = vec![token.trim_matches('"')];
        while iter.next_if_eq(&"/").is_some() {
            let alternative = iter.next()?;
            group = group.join(natural_term(alternative)?, true);
            alternatives.push(alternative.trim_matches('"'));
        }
        if is_excluded {
            let mut ex_terms = Vec::new();
            group.positive(false, &mut ex_terms);
            for term in ex_terms {
                excluded_words.insert(term.text.to_lowercase());
            }
            excluded.push(group);
            if inferred_neg {
                inferred.push(alternatives.join(" / "));
            }
        } else if !raw_groups.contains(&group) {
            raw_groups.push(group);
        }
    }

    let content_group = |group: &Node| {
        !matches!(
            group,
            Node::Term(term) if !term.prefix && !term.phrase && is_stop_word(&term.text)
        )
    };
    if raw_groups.iter().any(&content_group) {
        raw_groups.retain(content_group);
    }
    if raw_groups.is_empty() {
        return None;
    }

    let mut expanded_concepts = Vec::new();
    let groups = raw_groups
        .into_iter()
        .map(|group| match group {
            Node::Term(ref term) if !term.prefix && !term.phrase && !term.verbatim => {
                let syns: Vec<&'static str> = expand_synonyms(&term.text)
                    .into_iter()
                    .filter(|syn| !excluded_words.contains(*syn))
                    .collect();
                if syns.is_empty() {
                    group
                } else {
                    if !expanded_concepts.contains(&term.text) {
                        expanded_concepts.push(term.text.clone());
                    }
                    syns.into_iter().fold(group, |acc, syn| {
                        acc.join(
                            Node::Term(Term {
                                text: syn.to_owned(),
                                prefix: false,
                                phrase: true,
                                verbatim: true,
                            }),
                            true,
                        )
                    })
                }
            }
            other => other,
        })
        .collect();

    Some(SmartQuery {
        groups,
        excluded,
        inferred,
        facets,
        cleaned_query: cleaned_query.to_owned(),
        expanded_concepts,
    })
}

fn natural_term(token: &str) -> Option<Node> {
    let phrase = token.starts_with('"');
    let prefix = token.ends_with('*');
    let text = words(token).collect::<Vec<_>>().join(" ");
    (!text.is_empty()).then_some(Node::Term(Term {
        text: text.to_lowercase(),
        prefix,
        phrase,
        verbatim: phrase,
    }))
}

// --- Bilingual Concept Synonym Expansion ---

pub fn concept_synonyms(term: &str) -> &'static [&'static str] {
    let folded = fold_concept_key(term);
    match folded.as_str() {
        // 1. Invoice / Bill
        "invoice" | "invoices" | "bill" | "bills" | "billing" | "factuur" | "facturen"
        | "rekening" | "rekeningen" => &["invoice", "bill", "factuur", "facturen", "rekening"],
        // 2. Receipt
        "receipt" | "receipts" | "bon" | "bonnen" | "bonnetje" | "kassabon" | "kwitantie"
        | "kwitanties" => &["receipt", "bon", "kassabon", "kwitantie"],
        // 3. Tax / VAT
        "tax" | "taxes" | "taxation" | "vat" | "btw" | "belasting" | "belastingen"
        | "belastingdienst" | "aangifte" => &["tax", "taxes", "vat", "btw", "belasting", "aangifte"],
        // 4. Salary / Payslip
        "salary" | "salaries" | "payslip" | "payslips" | "paycheck" | "wage" | "wages"
        | "loonstrook" | "loonstrookje" | "salaris" | "loon" => {
            &["salary", "payslip", "wage", "loonstrook", "salaris", "loon"]
        }
        // 5. Contract / Agreement
        "contract" | "contracts" | "agreement" | "agreements" | "overeenkomst"
        | "overeenkomsten" | "contracten" => {
            &["contract", "agreement", "overeenkomst", "overeenkomsten"]
        }
        // 6. Car / Vehicle
        "car" | "cars" | "vehicle" | "vehicles" | "automobile" | "auto" | "autos"
        | "fahrzeug" | "voertuig" => &["car", "vehicle", "automobile", "auto"],
        // 7. Buy / Purchase
        "buy" | "purchase" | "purchased" | "bought" | "koop" | "kopen" | "gekocht"
        | "aankoop" | "aankopen" | "aanschaf" => &["buy", "purchase", "bought", "koop", "aankoop"],
        // 8. Rent / Lease
        "rent" | "rental" | "lease" | "landlord" | "tenant" | "huur" | "huren"
        | "huurcontract" | "verhuurder" => &["rent", "lease", "landlord", "huur", "huurcontract"],
        // 9. Insurance / Policy
        "insurance" | "insurer" | "policy" | "policies" | "verzekering" | "verzekeringen"
        | "polis" | "zorgverzekering" => &["insurance", "policy", "verzekering", "polis"],
        // 10. Resume / CV
        "resume" | "cv" | "curriculum" | "sollicitatie" | "sollicitatiebrief"
        | "motivatiebrief" => &["resume", "cv", "curriculum", "sollicitatie"],
        // 11. Budget / Forecast
        "budget" | "budgets" | "forecast" | "forecasts" | "begroting" | "begrotingen" => {
            &["budget", "forecast", "begroting"]
        }
        // 12. Password / Credentials
        "password" | "passwords" | "credentials" | "secret" | "secrets" | "passcode"
        | "wachtwoord" | "wachtwoorden" | "inloggegevens" => {
            &["password", "credentials", "secret", "wachtwoord", "wachtwoorden"]
        }
        // 13. Meeting / Minutes
        "meeting" | "meetings" | "minutes" | "notes" | "agenda" | "notulen" | "vergadering"
        | "vergaderingen" | "aantekeningen" => {
            &["meeting", "minutes", "notes", "notulen", "vergadering"]
        }
        // 14. Quote / Estimate / Proposal
        "quote" | "quotes" | "quotation" | "estimate" | "estimates" | "proposal"
        | "proposals" | "offerte" | "offertes" => {
            &["quote", "estimate", "proposal", "offerte", "offertes"]
        }
        // 15. Mortgage / Loan
        "mortgage" | "mortgages" | "loan" | "loans" | "hypotheek" | "hypotheken" | "lening"
        | "leningen" => &["mortgage", "loan", "hypotheek", "lening"],
        // 16. Warranty / Guarantee
        "warranty" | "warranties" | "guarantee" | "garantie" | "garantiebewijs" => {
            &["warranty", "guarantee", "garantie", "garantiebewijs"]
        }
        // 17. Manual / Guide
        "manual" | "manuals" | "guide" | "guides" | "handbook" | "instructions"
        | "handleiding" | "handleidingen" | "gebruiksaanwijzing" => {
            &["manual", "guide", "handbook", "handleiding", "gebruiksaanwijzing"]
        }
        // 18. Medical / Doctor / Hospital
        "medical" | "doctor" | "doctors" | "hospital" | "clinic" | "health" | "prescription"
        | "ziekenhuis" | "arts" | "huisarts" | "medisch" => {
            &["medical", "doctor", "hospital", "ziekenhuis", "arts", "huisarts"]
        }
        // 19. Bank / Statement
        "bank" | "banking" | "statement" | "statements" | "iban" | "bankafschrift"
        | "afschrift" | "afschriften" => {
            &["bank", "statement", "iban", "bankafschrift", "afschrift"]
        }
        // 20. Passport / Identity
        "passport" | "passports" | "paspoort" | "identiteitsbewijs" | "idkaart"
        | "rijbewijs" => &["passport", "paspoort", "identiteitsbewijs", "rijbewijs"],
        // 21. Travel / Flight / Booking
        "flight" | "flights" | "ticket" | "tickets" | "booking" | "itinerary" | "boarding"
        | "vlucht" | "boeking" | "reservering" => {
            &["flight", "ticket", "booking", "itinerary", "vlucht", "boeking", "reservering"]
        }
        // 22. Diploma / Certificate
        "diploma" | "diplomas" | "certificate" | "certificates" | "degree" | "transcript"
        | "certificaat" | "getuigschrift" | "cijferlijst" => {
            &["diploma", "certificate", "degree", "certificaat", "getuigschrift"]
        }
        // 23. Utility / Energy
        "utility" | "utilities" | "electricity" | "energy" | "energie" | "stroom"
        | "jaarafrekening" => {
            &["utility", "electricity", "energy", "energie", "stroom", "jaarafrekening"]
        }
        // 24. Pension / Retirement
        "pension" | "pensions" | "retirement" | "pensioen" | "pensioenoverzicht" => {
            &["pension", "retirement", "pensioen", "pensioenoverzicht"]
        }
        // 25. Shipping / Delivery / Parcel
        "shipping" | "shipment" | "delivery" | "tracking" | "parcel" | "pakket"
        | "verzending" | "bezorging" | "pakbon" => {
            &["shipping", "delivery", "tracking", "parcel", "pakket", "verzending", "bezorging"]
        }
        // 26. Subscription / Membership
        "subscription" | "subscriptions" | "membership" | "cancellation" | "abonnement"
        | "abonnementen" | "lidmaatschap" | "opzegging" => {
            &["subscription", "membership", "abonnement", "lidmaatschap", "opzegging"]
        }
        _ => &[],
    }
}

pub fn expand_synonyms(term: &str) -> Vec<&'static str> {
    let trimmed = term.trim();
    concept_synonyms(trimmed)
        .iter()
        .copied()
        .filter(|candidate| !candidate.eq_ignore_ascii_case(trimmed))
        .collect()
}

fn fold_concept_key(term: &str) -> String {
    term.trim()
        .chars()
        .filter(|c| c.is_alphanumeric())
        .flat_map(char::to_lowercase)
        .map(|c| match c {
            'é' | 'è' | 'ê' | 'ë' => 'e',
            'á' | 'à' | 'â' | 'ä' => 'a',
            'í' | 'ì' | 'î' | 'ï' => 'i',
            'ó' | 'ò' | 'ô' | 'ö' => 'o',
            'ú' | 'ù' | 'û' | 'ü' => 'u',
            other => other,
        })
        .collect()
}

// --- Natural-Language Intent & Facet Extraction ---

#[derive(Clone, Debug)]
struct RawToken<'a> {
    raw: &'a str,
    quoted: bool,
}

#[derive(Clone, Debug)]
enum FacetAction {
    Extensions { exts: Vec<&'static str>, label: String },
    ExplicitExtensions { exts: Vec<String>, label: String },
    MinBytes { bytes: i64, label: String },
    MaxBytes { bytes: i64, label: String },
    ByteRange { min: i64, max: i64, label: String },
    ModifiedAfter { ts: i64, label: String },
    ModifiedBefore { ts: i64, label: String },
    ModifiedRange { after: i64, before: i64, label: String },
    Tags { tags: Vec<String> },
    Online(bool),
}

#[derive(Clone, Debug)]
struct CandidateSpan {
    start: usize,
    end: usize,
    explicit: bool,
    multi_word_date: bool,
    paired_size_noun: bool,
    action: FacetAction,
}

impl CandidateSpan {
    fn apply(&self, facets: &mut QueryFacets, token_text: &str) {
        match &self.action {
            FacetAction::Extensions { exts, label } => {
                facets.add_extensions(exts, label.clone(), token_text);
            }
            FacetAction::ExplicitExtensions { exts, label } => {
                let refs: Vec<&str> = exts.iter().map(String::as_str).collect();
                facets.add_extensions(&refs, label.clone(), token_text);
            }
            FacetAction::MinBytes { bytes, label } => {
                facets.set_min_bytes(*bytes, label.clone(), token_text);
            }
            FacetAction::MaxBytes { bytes, label } => {
                facets.set_max_bytes(*bytes, label.clone(), token_text);
            }
            FacetAction::ByteRange { min, max, label } => {
                facets.min_bytes = Some(facets.min_bytes.map_or(*min, |prev| prev.max(*min)));
                facets.max_bytes = Some(facets.max_bytes.map_or(*max, |prev| prev.min(*max)));
                facets.chips.push(FacetChip {
                    kind: "size".into(),
                    label: label.clone(),
                    token: token_text.into(),
                });
            }
            FacetAction::ModifiedAfter { ts, label } => {
                facets.set_modified_after(*ts, label.clone(), token_text);
            }
            FacetAction::ModifiedBefore { ts, label } => {
                facets.set_modified_before(*ts, label.clone(), token_text);
            }
            FacetAction::ModifiedRange { after, before, label } => {
                facets.set_modified_range(*after, *before, label.clone(), token_text);
            }
            FacetAction::Tags { tags } => {
                for tag in tags {
                    facets.add_tag(tag, token_text);
                }
            }
            FacetAction::Online(online) => {
                facets.set_online(*online, token_text);
            }
        }
    }
}

pub fn extract_facets(query: &str, now_ms: i64) -> (String, QueryFacets) {
    extract_facets_impl(query, now_ms, true)
}

fn extract_explicit_facets_only(query: &str, now_ms: i64) -> (String, QueryFacets) {
    extract_facets_impl(query, now_ms, false)
}

fn extract_facets_impl(query: &str, now_ms: i64, allow_nl: bool) -> (String, QueryFacets) {
    let tokens = scan_raw_tokens(query);
    if tokens.is_empty() {
        return (String::new(), QueryFacets::default());
    }

    let mut candidates: Vec<CandidateSpan> = Vec::new();
    let mut index = 0;
    while index < tokens.len() {
        let negated = is_token_negated(&tokens, index);
        if !tokens[index].quoted && !negated {
            if let Some(explicit) = match_explicit_token(tokens[index].raw, index, now_ms) {
                index = explicit.end;
                candidates.push(explicit);
                continue;
            }
            if allow_nl {
                if let Some(nl_span) = match_nl_span(&tokens, index, now_ms) {
                    index = nl_span.end;
                    candidates.push(nl_span);
                    continue;
                }
            }
        }
        index += 1;
    }

    if candidates.is_empty() {
        let joined = tokens.iter().map(|t| t.raw).collect::<Vec<_>>().join(" ");
        return (joined, QueryFacets::default());
    }

    let mut claimed = vec![false; tokens.len()];
    for span in &candidates {
        for slot in &mut claimed[span.start..span.end] {
            *slot = true;
        }
    }

    let has_other_positive_content = has_positive_content_outside(&tokens, &claimed);
    let total_other_tokens = claimed.iter().filter(|&&c| !c).count();
    let multi_token_query = tokens.len() > 1;
    let total_facet_signals = candidates.len();

    let mut accepted = vec![false; candidates.len()];
    for (ci, span) in candidates.iter().enumerate() {
        accepted[ci] = if span.explicit || span.multi_word_date || span.paired_size_noun {
            true
        } else {
            // Single-word NL file-type, size, or today/yesterday hints:
            // Strip when accompanied by at least one positive content word or another filter,
            // or for size/today/yesterday in multi-word queries when other tokens exist,
            // ensuring single-word queries like "pdf", "large", or "today" never become empty.
            has_other_positive_content
                || total_facet_signals >= 2
                || (multi_token_query
                    && total_other_tokens >= 1
                    && !matches!(span.action, FacetAction::Extensions { .. }))
        };
    }

    let mut removed = vec![false; tokens.len()];
    let mut facets = QueryFacets::default();
    for (ci, span) in candidates.iter().enumerate() {
        if accepted[ci] {
            for slot in &mut removed[span.start..span.end] {
                *slot = true;
            }
            let raw_span = tokens[span.start..span.end]
                .iter()
                .map(|t| t.raw)
                .collect::<Vec<_>>()
                .join(" ");
            span.apply(&mut facets, &raw_span);
        }
    }

    let cleaned = tokens
        .iter()
        .enumerate()
        .filter_map(|(i, tok)| (!removed[i]).then_some(tok.raw))
        .collect::<Vec<_>>()
        .join(" ");
    (cleaned, facets)
}

fn scan_raw_tokens(input: &str) -> Vec<RawToken<'_>> {
    let mut tokens = Vec::new();
    let bytes = input.as_bytes();
    let mut i = 0;
    while i < input.len() {
        let ch = input[i..].chars().next().unwrap();
        if ch.is_whitespace() {
            i += ch.len_utf8();
            continue;
        }
        let start = i;
        let mut quoted = false;
        let mut in_quotes = false;
        while i < input.len() {
            let c = input[i..].chars().next().unwrap();
            if c == '"' {
                quoted = true;
                if in_quotes && bytes.get(i + 1) == Some(&b'"') {
                    i += 2;
                    continue;
                }
                in_quotes = !in_quotes;
                i += 1;
            } else if !in_quotes && c.is_whitespace() {
                break;
            } else {
                i += c.len_utf8();
            }
        }
        tokens.push(RawToken {
            raw: &input[start..i],
            quoted,
        });
    }
    tokens
}

fn is_token_negated(tokens: &[RawToken<'_>], index: usize) -> bool {
    let raw = tokens[index].raw;
    if raw.starts_with('-') && raw.len() > 1 {
        return true;
    }
    if index > 0 && !tokens[index - 1].quoted {
        let prev = tokens[index - 1].raw;
        if prev == "-" || is_negation_word(prev) || prev.eq_ignore_ascii_case("NOT") {
            return true;
        }
    }
    false
}

fn has_positive_content_outside(tokens: &[RawToken<'_>], claimed: &[bool]) -> bool {
    let mut skip_negated = false;
    for (i, tok) in tokens.iter().enumerate() {
        if claimed[i] {
            skip_negated = false;
            continue;
        }
        let raw = tok.raw;
        if !tok.quoted && (raw == "-" || is_negation_word(raw) || raw.eq_ignore_ascii_case("NOT")) {
            skip_negated = true;
            continue;
        }
        let negated_here = skip_negated || (!tok.quoted && raw.starts_with('-') && raw.len() > 1);
        skip_negated = false;
        if negated_here {
            continue;
        }
        if !tok.quoted
            && matches!(
                raw.to_ascii_uppercase().as_str(),
                "AND" | "OR" | "NOT" | "&&" | "|" | "||" | "(" | ")"
            )
        {
            continue;
        }
        if tok.quoted {
            if words(raw).next().is_some() {
                return true;
            }
        } else if words(raw).any(|w| !is_stop_word(w)) {
            return true;
        }
    }
    false
}

fn clean_nl_word(raw: &str) -> Option<String> {
    let trimmed = raw.trim_end_matches(',');
    if trimmed.is_empty()
        || trimmed.chars().any(|c| !c.is_alphanumeric())
    {
        return None;
    }
    Some(trimmed.to_lowercase())
}

fn is_container_noun(word: &str) -> bool {
    matches!(
        word,
        "file" | "files" | "document" | "documents" | "doc" | "docs" | "bestand" | "bestanden"
    )
}

fn match_explicit_token(raw: &str, index: usize, now_ms: i64) -> Option<CandidateSpan> {
    // Shorthand hashtag: #work
    if let Some(tag) = raw.strip_prefix('#') {
        let mut chars = tag.chars();
        if chars.next().is_some_and(char::is_alphabetic)
            && chars.all(|c| c.is_alphanumeric() || c == '_' || c == '-')
        {
            return Some(CandidateSpan {
                start: index,
                end: index + 1,
                explicit: true,
                multi_word_date: false,
                paired_size_noun: false,
                action: FacetAction::Tags {
                    tags: vec![tag.to_owned()],
                },
            });
        }
    }

    // Bare size comparators with explicit byte units: >10mb, >=500kb, <2gb, <=100mb
    if raw.starts_with('>') || raw.starts_with('<') {
        if let Some(action) = parse_size_comparator(raw, true) {
            return Some(CandidateSpan {
                start: index,
                end: index + 1,
                explicit: true,
                multi_word_date: false,
                paired_size_noun: false,
                action,
            });
        }
    }

    let (field, value) = raw.split_once(':')?;
    if value.is_empty() {
        return None;
    }
    let action = match field.to_ascii_lowercase().as_str() {
        "ext" | "type" => {
            let mut exts = Vec::new();
            for part in value.split(',') {
                let ext = part.trim().trim_start_matches('.').to_ascii_lowercase();
                if ext.is_empty()
                    || !ext
                        .chars()
                        .all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '+' || c == '-')
                {
                    return None;
                }
                if !exts.contains(&ext) {
                    exts.push(ext);
                }
            }
            let upper = exts
                .iter()
                .map(|e| e.to_ascii_uppercase())
                .collect::<Vec<_>>()
                .join(", ");
            FacetAction::ExplicitExtensions {
                exts,
                label: format!("Type: {upper}"),
            }
        }
        "tag" | "tags" => {
            let tags: Vec<String> = value
                .split(',')
                .map(str::trim)
                .map(|t| t.trim_start_matches('#'))
                .filter(|t| !t.is_empty())
                .map(ToOwned::to_owned)
                .collect();
            if tags.is_empty() {
                return None;
            }
            FacetAction::Tags { tags }
        }
        "size" => {
            if value.starts_with('>') || value.starts_with('<') {
                parse_size_comparator(value, false)?
            } else if let Some((lo, hi)) = value.split_once("..") {
                let min = parse_size_bytes(lo, false)?;
                let max = parse_size_bytes(hi, false)?;
                if min > max {
                    return None;
                }
                FacetAction::ByteRange {
                    min,
                    max,
                    label: format!(
                        "Size: {} – {}",
                        format_bytes_label(min),
                        format_bytes_label(max)
                    ),
                }
            } else {
                let exact = parse_size_bytes(value, false)?;
                FacetAction::ByteRange {
                    min: exact,
                    max: exact,
                    label: format!("Size: {}", format_bytes_label(exact)),
                }
            }
        }
        "min" => {
            let bytes = parse_size_bytes(value, false)?;
            FacetAction::MinBytes {
                bytes,
                label: format!("Size: >= {}", format_bytes_label(bytes)),
            }
        }
        "max" => {
            let bytes = parse_size_bytes(value, false)?;
            FacetAction::MaxBytes {
                bytes,
                label: format!("Size: <= {}", format_bytes_label(bytes)),
            }
        }
        "modified" => parse_modified_value(value, now_ms)?,
        "after" | "since" => {
            let (start, _) = parse_iso_date_utc_ms(value)?;
            FacetAction::ModifiedAfter {
                ts: start,
                label: format!("Modified: After {value}"),
            }
        }
        "before" => {
            let (start, _) = parse_iso_date_utc_ms(value)?;
            FacetAction::ModifiedBefore {
                ts: start,
                label: format!("Modified: Before {value}"),
            }
        }
        "status" => match value.to_ascii_lowercase().as_str() {
            "online" => FacetAction::Online(true),
            "offline" => FacetAction::Online(false),
            _ => return None,
        },
        "online" => match value.to_ascii_lowercase().as_str() {
            "true" | "1" | "yes" => FacetAction::Online(true),
            "false" | "0" | "no" => FacetAction::Online(false),
            _ => return None,
        },
        _ => return None,
    };

    Some(CandidateSpan {
        start: index,
        end: index + 1,
        explicit: true,
        multi_word_date: false,
        paired_size_noun: false,
        action,
    })
}

fn parse_size_comparator(raw: &str, require_unit: bool) -> Option<FacetAction> {
    if let Some(rest) = raw.strip_prefix(">=") {
        let bytes = parse_size_bytes(rest, require_unit)?;
        Some(FacetAction::MinBytes {
            bytes,
            label: format!("Size: >= {}", format_bytes_label(bytes)),
        })
    } else if let Some(rest) = raw.strip_prefix('>') {
        let bytes = parse_size_bytes(rest, require_unit)?;
        Some(FacetAction::MinBytes {
            bytes,
            label: format!("Size: > {}", format_bytes_label(bytes)),
        })
    } else if let Some(rest) = raw.strip_prefix("<=") {
        let bytes = parse_size_bytes(rest, require_unit)?;
        Some(FacetAction::MaxBytes {
            bytes,
            label: format!("Size: <= {}", format_bytes_label(bytes)),
        })
    } else if let Some(rest) = raw.strip_prefix('<') {
        let bytes = parse_size_bytes(rest, require_unit)?;
        Some(FacetAction::MaxBytes {
            bytes,
            label: format!("Size: < {}", format_bytes_label(bytes)),
        })
    } else {
        None
    }
}

fn parse_size_bytes(raw: &str, require_unit: bool) -> Option<i64> {
    let raw = raw.trim();
    if raw.is_empty() {
        return None;
    }
    let split = raw
        .find(|c: char| !c.is_ascii_digit() && c != '.')
        .unwrap_or(raw.len());
    let (number, unit) = raw.split_at(split);
    if number.is_empty()
        || number.starts_with('.')
        || number.ends_with('.')
        || number.bytes().filter(|&b| b == b'.').count() > 1
    {
        return None;
    }
    let unit_lc = unit.trim().to_ascii_lowercase();
    let multiplier: i128 = match unit_lc.as_str() {
        "" if !require_unit => 1,
        "b" | "byte" | "bytes" => 1,
        "kb" => KB as i128,
        "mb" => MB as i128,
        "gb" => GB as i128,
        "tb" => TB as i128,
        "kib" => 1 << 10,
        "mib" => 1 << 20,
        "gib" => 1 << 30,
        "tib" => 1i128 << 40,
        "k" if !require_unit => KB as i128,
        "m" if !require_unit => MB as i128,
        "g" if !require_unit => GB as i128,
        "t" if !require_unit => TB as i128,
        _ => return None,
    };
    let (integer, fraction) = number.split_once('.').unwrap_or((number, ""));
    let base = integer.parse::<i128>().ok()?.checked_mul(multiplier)?;
    let fraction = fraction.trim_end_matches('0');
    let extra = if fraction.is_empty() {
        0
    } else {
        let divisor = 10i128.checked_pow(fraction.len() as u32)?;
        let scaled = fraction.parse::<i128>().ok()?.checked_mul(multiplier)?;
        scaled / divisor
    };
    i64::try_from(base.checked_add(extra)?).ok()
}

fn format_bytes_label(bytes: i64) -> String {
    if bytes >= TB && bytes % TB == 0 {
        format!("{} TB", bytes / TB)
    } else if bytes >= GB && bytes % GB == 0 {
        format!("{} GB", bytes / GB)
    } else if bytes >= MB && bytes % MB == 0 {
        format!("{} MB", bytes / MB)
    } else if bytes >= KB && bytes % KB == 0 {
        format!("{} KB", bytes / KB)
    } else if bytes >= (1 << 30) && bytes % (1 << 30) == 0 {
        format!("{} GiB", bytes / (1 << 30))
    } else if bytes >= (1 << 20) && bytes % (1 << 20) == 0 {
        format!("{} MiB", bytes / (1 << 20))
    } else if bytes >= (1 << 10) && bytes % (1 << 10) == 0 {
        format!("{} KiB", bytes / (1 << 10))
    } else {
        format!("{bytes} B")
    }
}

fn parse_modified_value(value: &str, now_ms: i64) -> Option<FacetAction> {
    if let Some(rest) = value.strip_prefix('>') {
        let (start, _) = parse_iso_date_utc_ms(rest)?;
        return Some(FacetAction::ModifiedAfter {
            ts: start,
            label: format!("Modified: After {rest}"),
        });
    }
    if let Some(rest) = value.strip_prefix('<') {
        let (start, _) = parse_iso_date_utc_ms(rest)?;
        return Some(FacetAction::ModifiedBefore {
            ts: start,
            label: format!("Modified: Before {rest}"),
        });
    }
    if let Some((start, end)) = parse_iso_date_utc_ms(value) {
        return Some(FacetAction::ModifiedRange {
            after: start,
            before: end,
            label: format!("Modified: {value}"),
        });
    }
    let split = value
        .find(|c: char| !c.is_ascii_digit())
        .unwrap_or(value.len());
    let (num_str, unit) = value.split_at(split);
    let count: i64 = num_str.parse().ok()?;
    if count <= 0 {
        return None;
    }
    let (delta_ms, suffix) = match unit.to_ascii_lowercase().as_str() {
        "h" => (count.checked_mul(HOUR_MS)?, "h"),
        "d" => (count.checked_mul(DAY_MS)?, "d"),
        "w" => (count.checked_mul(7 * DAY_MS)?, "w"),
        "m" | "mo" => (count.checked_mul(30 * DAY_MS)?, "mo"),
        "y" => (count.checked_mul(365 * DAY_MS)?, "y"),
        _ => return None,
    };
    Some(FacetAction::ModifiedAfter {
        ts: now_ms.saturating_sub(delta_ms),
        label: format!("Modified: Last {count}{suffix}"),
    })
}

fn ymd_utc_ms(year: i32, month: u32, day: u32) -> Option<i64> {
    let date = NaiveDate::from_ymd_opt(year, month, day)?;
    let epoch = NaiveDate::from_ymd_opt(1970, 1, 1)?;
    Some(date.signed_duration_since(epoch).num_days() * DAY_MS)
}

fn parse_iso_date_utc_ms(raw: &str) -> Option<(i64, i64)> {
    let raw = raw.trim();
    if let Ok(date) = NaiveDate::parse_from_str(raw, "%Y-%m-%d") {
        let epoch = NaiveDate::from_ymd_opt(1970, 1, 1)?;
        let start = date.signed_duration_since(epoch).num_days() * DAY_MS;
        return Some((start, start + DAY_MS));
    }
    if raw.len() == 7 && raw.as_bytes().get(4) == Some(&b'-') {
        let (y_str, m_str) = raw.split_once('-')?;
        let year: i32 = y_str.parse().ok()?;
        let month: u32 = m_str.parse().ok()?;
        if (1970..=2100).contains(&year) && (1..=12).contains(&month) {
            let start = ymd_utc_ms(year, month, 1)?;
            let (ny, nm) = if month == 12 { (year + 1, 1) } else { (year, month + 1) };
            let end = ymd_utc_ms(ny, nm, 1)?;
            return Some((start, end));
        }
    }
    if raw.len() == 4 && raw.bytes().all(|b| b.is_ascii_digit()) {
        let year: i32 = raw.parse().ok()?;
        if (1970..=2100).contains(&year) {
            return Some((ymd_utc_ms(year, 1, 1)?, ymd_utc_ms(year + 1, 1, 1)?));
        }
    }
    None
}

fn nl_word_at(tokens: &[RawToken<'_>], idx: usize) -> Option<String> {
    let tok = tokens.get(idx)?;
    if tok.quoted || is_token_negated(tokens, idx) {
        return None;
    }
    clean_nl_word(tok.raw)
}

fn match_nl_span(tokens: &[RawToken<'_>], index: usize, now_ms: i64) -> Option<CandidateSpan> {
    let w0 = nl_word_at(tokens, index)?;
    let w1 = nl_word_at(tokens, index + 1);
    let w2 = nl_word_at(tokens, index + 2);
    let w3 = nl_word_at(tokens, index + 3);

    // 4-word date phrases: "in the last month", "from the past week", etc.
    if let (Some(a), Some(b), Some(c)) = (w1.as_deref(), w2.as_deref(), w3.as_deref()) {
        if matches!(w0.as_str(), "in" | "from" | "over" | "during")
            && a == "the"
            && matches!(b, "past" | "last")
        {
            if let Some(action) = relative_period_action(b, c, now_ms) {
                return Some(CandidateSpan {
                    start: index,
                    end: index + 4,
                    explicit: false,
                    multi_word_date: true,
                    paired_size_noun: false,
                    action,
                });
            }
        }
    }

    // 3-word date phrases: "from last month", "in this week", "uit vorige maand", "vanaf vorig jaar"
    if let (Some(a), Some(b)) = (w1.as_deref(), w2.as_deref()) {
        if matches!(
            w0.as_str(),
            "from" | "in" | "since" | "during" | "uit" | "van" | "sinds" | "vanaf"
        ) {
            if let Some(action) = relative_period_action(a, b, now_ms) {
                return Some(CandidateSpan {
                    start: index,
                    end: index + 3,
                    explicit: false,
                    multi_word_date: true,
                    paired_size_noun: false,
                    action,
                });
            }
        }
    }

    // 2-word date phrases
    if let Some(a) = w1.as_deref() {
        if let Some(action) = relative_period_action(&w0, a, now_ms) {
            return Some(CandidateSpan {
                start: index,
                end: index + 2,
                explicit: false,
                multi_word_date: true,
                paired_size_noun: false,
                action,
            });
        }

        // Preposition + today / yesterday
        if matches!(
            w0.as_str(),
            "from" | "since" | "modified" | "updated" | "van" | "sinds" | "vanaf"
        ) {
            if matches!(a, "today" | "vandaag") {
                return Some(CandidateSpan {
                    start: index,
                    end: index + 2,
                    explicit: false,
                    multi_word_date: true,
                    paired_size_noun: false,
                    action: FacetAction::ModifiedAfter {
                        ts: now_ms.saturating_sub(DAY_MS),
                        label: "Modified: Today".into(),
                    },
                });
            }
            if matches!(a, "yesterday" | "gisteren") {
                return Some(CandidateSpan {
                    start: index,
                    end: index + 2,
                    explicit: false,
                    multi_word_date: true,
                    paired_size_noun: false,
                    action: FacetAction::ModifiedAfter {
                        ts: now_ms.saturating_sub(2 * DAY_MS),
                        label: "Modified: Since yesterday".into(),
                    },
                });
            }
        }

        // Prepositional years: "from 2024", "in 2024", "uit 2024", "since 2023", "vanaf 2023"
        if a.len() == 4 && a.bytes().all(|b| b.is_ascii_digit()) {
            if let Ok(year) = a.parse::<i32>() {
                if (1970..=2100).contains(&year) {
                    if matches!(w0.as_str(), "from" | "in" | "uit" | "van") {
                        let start = ymd_utc_ms(year, 1, 1)?;
                        let end = ymd_utc_ms(year + 1, 1, 1)?;
                        return Some(CandidateSpan {
                            start: index,
                            end: index + 2,
                            explicit: false,
                            multi_word_date: true,
                            paired_size_noun: false,
                            action: FacetAction::ModifiedRange {
                                after: start,
                                before: end,
                                label: format!("Modified: {year}"),
                            },
                        });
                    }
                    if matches!(w0.as_str(), "since" | "after" | "vanaf" | "sinds" | "na") {
                        let start = ymd_utc_ms(year, 1, 1)?;
                        return Some(CandidateSpan {
                            start: index,
                            end: index + 2,
                            explicit: false,
                            multi_word_date: true,
                            paired_size_noun: false,
                            action: FacetAction::ModifiedAfter {
                                ts: start,
                                label: format!("Modified: Since {year}"),
                            },
                        });
                    }
                    if w0 == "before" {
                        let start = ymd_utc_ms(year, 1, 1)?;
                        return Some(CandidateSpan {
                            start: index,
                            end: index + 2,
                            explicit: false,
                            multi_word_date: true,
                            paired_size_noun: false,
                            action: FacetAction::ModifiedBefore {
                                ts: start,
                                label: format!("Modified: Before {year}"),
                            },
                        });
                    }
                }
            }
        }
    }

    // 1-word relative dates: today / yesterday / vandaag / gisteren
    if matches!(w0.as_str(), "today" | "vandaag") {
        return Some(CandidateSpan {
            start: index,
            end: index + 1,
            explicit: false,
            multi_word_date: false,
            paired_size_noun: false,
            action: FacetAction::ModifiedAfter {
                ts: now_ms.saturating_sub(DAY_MS),
                label: "Modified: Today".into(),
            },
        });
    }
    if matches!(w0.as_str(), "yesterday" | "gisteren") {
        return Some(CandidateSpan {
            start: index,
            end: index + 1,
            explicit: false,
            multi_word_date: false,
            paired_size_noun: false,
            action: FacetAction::ModifiedAfter {
                ts: now_ms.saturating_sub(2 * DAY_MS),
                label: "Modified: Since yesterday".into(),
            },
        });
    }

    // NL Size hints (optionally paired with a container noun like "large files", "groot bestand")
    if let Some(action) = nl_size_action(&w0) {
        let paired = w1.as_deref().is_some_and(is_container_noun);
        return Some(CandidateSpan {
            start: index,
            end: index + if paired { 2 } else { 1 },
            explicit: false,
            multi_word_date: false,
            paired_size_noun: paired,
            action,
        });
    }

    // NL File Type facets (optionally followed by a container noun like "pdf files")
    if let Some(action) = nl_file_type_action(&w0) {
        let paired = w1.as_deref().is_some_and(is_container_noun);
        return Some(CandidateSpan {
            start: index,
            end: index + if paired { 2 } else { 1 },
            explicit: false,
            multi_word_date: false,
            paired_size_noun: false,
            action,
        });
    }

    None
}

fn relative_period_action(qualifier: &str, unit: &str, now_ms: i64) -> Option<FacetAction> {
    let en_qual = matches!(qualifier, "this" | "last" | "past");
    let nl_de = matches!(qualifier, "deze" | "vorige" | "afgelopen");
    let nl_het = matches!(qualifier, "dit" | "vorig" | "afgelopen");

    if (en_qual || nl_de) && unit == "week" {
        return Some(FacetAction::ModifiedAfter {
            ts: now_ms.saturating_sub(7 * DAY_MS),
            label: "Modified: Last 7d".into(),
        });
    }
    if (en_qual && unit == "month") || (nl_de && unit == "maand") {
        return Some(FacetAction::ModifiedAfter {
            ts: now_ms.saturating_sub(30 * DAY_MS),
            label: "Modified: Last 30d".into(),
        });
    }
    if (en_qual && unit == "year") || (nl_het && unit == "jaar") {
        return Some(FacetAction::ModifiedAfter {
            ts: now_ms.saturating_sub(365 * DAY_MS),
            label: "Modified: Last year".into(),
        });
    }
    None
}

fn nl_size_action(word: &str) -> Option<FacetAction> {
    match word {
        "huge" | "enorm" | "enorme" => Some(FacetAction::MinBytes {
            bytes: 100 * MB,
            label: "Size: > 100 MB".into(),
        }),
        "large" | "big" | "groot" | "grote" => Some(FacetAction::MinBytes {
            bytes: 10 * MB,
            label: "Size: > 10 MB".into(),
        }),
        "tiny" | "small" | "klein" | "kleine" => Some(FacetAction::MaxBytes {
            bytes: 100 * KB,
            label: "Size: < 100 KB".into(),
        }),
        _ => None,
    }
}

fn nl_file_type_action(word: &str) -> Option<FacetAction> {
    match word {
        "pdf" | "pdfs" => Some(FacetAction::Extensions {
            exts: vec!["pdf"],
            label: "Type: PDF".into(),
        }),
        "word" | "docx" => Some(FacetAction::Extensions {
            exts: vec!["docx", "odt"],
            label: "Type: Word".into(),
        }),
        "excel" | "spreadsheet" | "spreadsheets" | "werkblad" | "werkbladen" => {
            Some(FacetAction::Extensions {
                exts: vec!["xlsx", "csv", "ods"],
                label: "Type: Spreadsheet".into(),
            })
        }
        "powerpoint" | "presentation" | "presentations" | "slides" | "presentatie"
        | "presentaties" => Some(FacetAction::Extensions {
            exts: vec!["pptx", "odp"],
            label: "Type: Slides".into(),
        }),
        "markdown" => Some(FacetAction::Extensions {
            exts: vec!["md"],
            label: "Type: Markdown".into(),
        }),
        "code" | "script" | "scripts" | "source" => Some(FacetAction::Extensions {
            exts: vec![
                "rs", "cs", "py", "js", "ts", "tsx", "jsx", "c", "cpp", "h", "go", "java", "ps1",
                "sh", "sql",
            ],
            label: "Type: Code".into(),
        }),
        _ => None,
    }
}

// --- Boolean Parser ---

struct Parser<'a> {
    tokens: Vec<&'a str>,
    index: usize,
    explicit: bool,
    any: bool,
}

impl<'a> Parser<'a> {
    fn current(&self) -> &'a str {
        self.tokens.get(self.index).copied().unwrap_or("")
    }

    fn operand_starts(&self) -> bool {
        !matches!(
            self.current().to_ascii_uppercase().as_str(),
            "" | ")" | "AND" | "&&" | "OR" | "|" | "||"
        )
    }

    fn or(&mut self, depth: u8) -> Option<Node> {
        let mut left = self.adjacent(depth)?;
        while matches!(self.current().to_ascii_uppercase().as_str(), "OR" | "|" | "||") {
            self.explicit = true;
            self.index += 1;
            left = left.join(self.adjacent(depth)?, true);
        }
        Some(left)
    }

    fn adjacent(&mut self, depth: u8) -> Option<Node> {
        let first = self.and(depth)?;
        if !self.any || !self.operand_starts() {
            return Some(first);
        }
        let (mut include, mut exclude) = (None, None);
        let mut current = Some(first);
        while let Some(node) = current {
            let (slot, node) = match node {
                Node::Not(node) => (&mut exclude, *node),
                _ => (&mut include, node),
            };
            *slot = Some(match slot.take() {
                Some(previous) => Node::join(previous, node, true),
                None => node,
            });
            current = if self.operand_starts() {
                Some(self.and(depth)?)
            } else {
                None
            };
        }
        match (include, exclude) {
            (Some(a), Some(b)) => Some(a.join(Node::Not(Box::new(b)), false)),
            (None, Some(b)) => Some(Node::Not(Box::new(b))),
            (a, None) => a,
        }
    }

    fn and(&mut self, depth: u8) -> Option<Node> {
        let mut left = self.operand(depth)?;
        loop {
            let explicit = matches!(self.current().to_ascii_uppercase().as_str(), "AND" | "&&");
            if !explicit && (self.any || !self.operand_starts()) {
                break;
            }
            if explicit {
                self.explicit = true;
                self.index += 1;
            }
            left = left.join(self.operand(depth)?, false);
        }
        Some(left)
    }

    fn operand(&mut self, depth: u8) -> Option<Node> {
        if depth >= 12 || !self.operand_starts() {
            return None;
        }
        let token = self.current();
        self.index += 1;
        match token.to_ascii_uppercase().as_str() {
            "NOT" | "-" => {
                self.explicit |= token != "-";
                Some(Node::Not(Box::new(self.operand(depth + 1)?)))
            }
            "(" => {
                self.explicit = true;
                let group = self.or(depth + 1)?;
                if self.current() != ")" {
                    return None;
                }
                self.index += 1;
                Some(group)
            }
            "\"" => None,
            _ => {
                let phrase = token.starts_with('"');
                let prefix = token.ends_with('*');
                let text = if phrase {
                    words(token).collect::<Vec<_>>().join(" ")
                } else {
                    token.trim_end_matches('*').to_owned()
                };
                (!text.is_empty()).then_some(Node::Term(Term {
                    text,
                    prefix,
                    phrase,
                    verbatim: false,
                }))
            }
        }
    }
}

pub fn distance(a: &str, b: &str) -> usize {
    let a: Vec<_> = a.chars().collect();
    let b: Vec<_> = b.chars().collect();
    let width = b.len() + 2;
    let infinity = a.len() + b.len();
    let mut scores = vec![infinity; (a.len() + 2) * width];
    for i in 0..=a.len() {
        scores[(i + 1) * width + 1] = i;
    }
    for j in 0..=b.len() {
        scores[width + j + 1] = j;
    }
    let mut previous_rows = HashMap::new();
    for (i, left) in (1..).zip(a) {
        let mut previous_column = 0;
        for (j, right) in (1..).zip(&b) {
            let same = left == *right;
            let previous_row = previous_rows.get(right).copied().unwrap_or(0);
            let transposed_column = previous_column;
            if same {
                previous_column = j;
            }
            let cell = (i + 1) * width + j + 1;
            let substitution = scores[cell - width - 1] + usize::from(!same);
            let insertion = scores[cell - 1] + 1;
            let deletion = scores[cell - width] + 1;
            let transposition = scores[previous_row * width + transposed_column]
                + (i - previous_row - 1)
                + 1
                + (j - transposed_column - 1);
            scores[cell] = substitution.min(insertion).min(deletion).min(transposition);
        }
        previous_rows.insert(left, i);
    }
    scores[scores.len() - 1]
}

#[cfg(test)]
mod tests {
    use super::*;

    const FIXED_NOW_MS: i64 = 1_760_000_000_000;

    #[test]
    fn edit_distance_counts_transpositions_and_insertions() {
        assert_eq!(distance("CA", "ABC"), 2);
        assert_eq!(distance("reciept", "receipt"), 1);
        assert_eq!(distance("niovce", "invoice"), 3);
        assert_eq!(distance("café", "cafe"), 1);
        assert_eq!(distance("", "abc"), 3);
    }

    #[test]
    fn english_nl_facets_and_exclusion() {
        let query = "large pdf invoice from last month without draft";
        let (cleaned, facets) = extract_facets(query, FIXED_NOW_MS);
        assert_eq!(cleaned, "invoice without draft");
        assert_eq!(facets.extensions, vec!["pdf"]);
        assert_eq!(facets.min_bytes, Some(10 * MB));
        assert_eq!(facets.max_bytes, None);
        assert_eq!(facets.modified_after, Some(FIXED_NOW_MS - 30 * DAY_MS));
        assert_eq!(facets.modified_before, None);
        assert_eq!(facets.chips.len(), 3);
        assert!(facets.chips.iter().any(|c| c.kind == "size" && c.label == "Size: > 10 MB"));
        assert!(facets.chips.iter().any(|c| c.kind == "ext" && c.label == "Type: PDF"));
        assert!(facets.chips.iter().any(|c| c.kind == "date" && c.label == "Modified: Last 30d"));

        let parsed = parse_with_now(query, ContentMatchMode::Smart, FIXED_NOW_MS).unwrap();
        let natural = parsed.natural.expect("expected SmartQuery");
        assert_eq!(natural.inferred, vec!["draft"]);
        assert_eq!(natural.groups.len(), 1);
        assert!(natural.expanded_concepts.contains(&"invoice".to_string()));
        let root_node = natural.root();
        let mut pos = Vec::new();
        root_node.positive(false, &mut pos);
        let texts: Vec<&str> = pos.iter().map(|t| t.text.as_str()).collect();
        assert!(texts.contains(&"invoice"));
        assert!(texts.contains(&"factuur"));
        assert!(!texts.contains(&"draft"));
    }

    #[test]
    fn dutch_nl_facets_and_exclusion() {
        let query = "grote excel begroting vorige maand zonder concept";
        let (cleaned, facets) = extract_facets(query, FIXED_NOW_MS);
        assert_eq!(cleaned, "begroting zonder concept");
        assert_eq!(facets.extensions, vec!["xlsx", "csv", "ods"]);
        assert_eq!(facets.min_bytes, Some(10 * MB));
        assert_eq!(facets.modified_after, Some(FIXED_NOW_MS - 30 * DAY_MS));
        assert_eq!(facets.chips.len(), 3);
        assert!(facets.chips.iter().any(|c| c.label == "Type: Spreadsheet"));

        let parsed = parse_with_now(query, ContentMatchMode::Smart, FIXED_NOW_MS).unwrap();
        let natural = parsed.natural.expect("expected SmartQuery");
        assert_eq!(natural.inferred, vec!["concept"]);
        assert_eq!(natural.groups.len(), 1);
        let root_node = natural.root();
        let mut pos = Vec::new();
        root_node.positive(false, &mut pos);
        let texts: Vec<&str> = pos.iter().map(|t| t.text.as_str()).collect();
        assert!(texts.contains(&"begroting"));
        assert!(texts.contains(&"budget"));
        assert!(texts.contains(&"forecast"));
    }

    #[test]
    fn explicit_structured_facets_and_years() {
        let query = "ext:docx,pdf tag:work #urgent size:>10mb <2gb modified:7d status:online contract";
        let (cleaned, facets) = extract_facets(query, FIXED_NOW_MS);
        assert_eq!(cleaned, "contract");
        assert_eq!(facets.extensions, vec!["docx", "pdf"]);
        assert_eq!(facets.tags, vec!["work", "urgent"]);
        assert_eq!(facets.min_bytes, Some(10 * MB));
        assert_eq!(facets.max_bytes, Some(2 * GB));
        assert_eq!(facets.modified_after, Some(FIXED_NOW_MS - 7 * DAY_MS));
        assert_eq!(facets.online, Some(true));

        let (cleaned_yr, yr_facets) = extract_facets("belasting uit 2024", FIXED_NOW_MS);
        assert_eq!(cleaned_yr, "belasting");
        assert_eq!(yr_facets.modified_after, ymd_utc_ms(2024, 1, 1));
        assert_eq!(yr_facets.modified_before, ymd_utc_ms(2025, 1, 1));

        let (cleaned_since, since_facets) =
            extract_facets("mortgage after:2025-01-01 before:2025-12-31 min:5mb max:100mb status:offline", FIXED_NOW_MS);
        assert_eq!(cleaned_since, "mortgage");
        assert_eq!(since_facets.modified_after, ymd_utc_ms(2025, 1, 1));
        assert_eq!(since_facets.modified_before, ymd_utc_ms(2025, 12, 31));
        assert_eq!(since_facets.min_bytes, Some(5 * MB));
        assert_eq!(since_facets.max_bytes, Some(100 * MB));
        assert_eq!(since_facets.online, Some(false));

        let (cleaned_vanaf, vanaf_facets) = extract_facets("huurcontract vanaf 2023", FIXED_NOW_MS);
        assert_eq!(cleaned_vanaf, "huurcontract");
        assert_eq!(vanaf_facets.modified_after, ymd_utc_ms(2023, 1, 1));
        assert_eq!(vanaf_facets.modified_before, None);

        let (cleaned_quotes, quote_facets) =
            extract_facets("tiny code \"large pdf\" yesterday", FIXED_NOW_MS);
        assert_eq!(cleaned_quotes, "\"large pdf\"");
        assert_eq!(quote_facets.max_bytes, Some(100 * KB));
        assert!(quote_facets.extensions.contains(&"rs".to_string()));
        assert_eq!(quote_facets.modified_after, Some(FIXED_NOW_MS - 2 * DAY_MS));
    }

    #[test]
    fn single_word_type_query_is_not_stripped_empty() {
        let (cleaned, facets) = extract_facets("pdf", FIXED_NOW_MS);
        assert_eq!(cleaned, "pdf");
        assert!(facets.is_empty());

        let (cleaned_today, facets_today) = extract_facets("today", FIXED_NOW_MS);
        assert_eq!(cleaned_today, "today");
        assert!(facets_today.is_empty());
    }

    #[test]
    fn bilingual_concept_synonyms_cover_clusters() {
        assert!(concept_synonyms("invoice").contains(&"factuur"));
        assert!(concept_synonyms("factuur").contains(&"invoice"));
        assert_eq!(
            concept_synonyms("fahrzeug"),
            &["car", "vehicle", "automobile", "auto"]
        );
        assert!(concept_synonyms("loonstrook").contains(&"payslip"));
        assert!(concept_synonyms("verzekering").contains(&"insurance"));
        assert!(concept_synonyms("wachtwoord").contains(&"password"));
        assert!(concept_synonyms("hypotheek").contains(&"mortgage"));
        assert!(concept_synonyms("handleiding").contains(&"manual"));
        assert!(concept_synonyms("ziekenhuis").contains(&"hospital"));
        assert!(concept_synonyms("nonexistentwordxyz").is_empty());
    }
}
