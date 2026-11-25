use std::{collections::HashMap, sync::OnceLock};
use crate::{content::ContentMatchMode, util::words};

#[derive(Clone, Debug, Eq, PartialEq, Hash)]
pub(crate) struct Term {
    pub text: String,
    pub prefix: bool,
    pub phrase: bool,
    verbatim: bool,
}

#[derive(Clone, Eq, PartialEq)]
pub(crate) enum Node {
    Term(Term),
    Not(Box<Node>),
    Join(Box<Node>,Box<Node>,bool),
}

impl Node {
    pub fn join(self, right: Self, any: bool) -> Self { Self::Join(Box::new(self),Box::new(right),any) }

    pub fn positive<'a>(&'a self, excluded: bool, terms: &mut Vec<&'a Term>) {
        match self {
            Self::Term(term) if !excluded && !terms.contains(&term) => terms.push(term),
            Self::Term(_) => {},
            Self::Not(node) => node.positive(!excluded,terms),
            Self::Join(a,b,_) => { a.positive(excluded,terms); b.positive(excluded,terms); }
        }
    }

    pub fn compile(&self, corrections: &HashMap<String,Vec<String>>, excluded: bool) -> (String,bool) {
        match self {
            Self::Term(term) => (term.render(corrections,excluded), false),
            Self::Not(node) => {
                let (expression, complement)=node.compile(corrections,!excluded);
                (expression,!complement)
            }
            Self::Join(a,b,any) => {
                let (mut a, ac)=a.compile(corrections,excluded);
                let (mut b, bc)=b.compile(corrections,excluded);
                if ac==bc {
                    let op=if *any!=ac {"OR"} else {"AND"};
                    (format!("({a} {op} {b})"),ac)
                } else {
                    if ac!=*any { std::mem::swap(&mut a,&mut b); }
                    (format!("({a} NOT {b})"),*any)
                }
            }
        }
    }
}

impl Term {
    pub fn render(&self,corrections:&HashMap<String,Vec<String>>,excluded:bool)->String {
        let exact=quote(&self.text,self.prefix);
        if excluded || self.prefix || self.verbatim || corrections.is_empty() { return exact; }
        if !self.phrase {
            return corrections.get(&self.text.to_lowercase()).map_or_else(||exact.clone(), |alternatives| {
                format!("({exact} OR {})",alternatives.iter().map(|word|quote(word,true)).collect::<Vec<_>>().join(" OR "))
            });
        }
        let mut phrases=vec![String::new()];
        let mut corrected=false;
        for word in words(&self.text) {
            let mut choices=vec![quote(word,false)];
            if let Some(alternatives)=corrections.get(&word.to_lowercase()) {
                corrected=true;
                choices.extend(alternatives.iter().map(|word|quote(word,true)));
            }
            phrases=phrases.iter().flat_map(|phrase|choices.iter().map(move |choice|
                if phrase.is_empty() { choice.clone() } else { format!("{phrase} + {choice}") }))
                .take(32).collect();
        }
        if corrected { format!("({exact} OR {})",phrases.join(" OR ")) } else { exact }
    }
}

pub(crate) fn quote(word:&str,prefix:bool)->String {
    format!("\"{}\"{}",word.replace('"',"\"\""),if prefix {"*"} else {""})
}

pub(crate) fn parse(input:&str,mode:ContentMatchMode)->Option<(Node,bool)> {
    if input.encode_utf16().count()>4096 { return None; }
    if mode==ContentMatchMode::ExactPhrase {
        let text=words(input).collect::<Vec<_>>().join(" ");
        return (!text.is_empty()).then_some((Node::Term(Term{text,prefix:false,phrase:true,verbatim:false}),false));
    }
    static TOKENS:OnceLock<regex::Regex>=OnceLock::new();
    let re=TOKENS.get_or_init(||regex::Regex::new(r#""[^"]*"|"|&&|\|\|?|[()-]|[\p{L}\p{N}][\p{L}\p{M}\p{N}]*\*?"#).unwrap());
    let tokens:Vec<_>=re.find_iter(input).filter(|m|{
        m.as_str()!="-" || !input[..m.start()].chars().next_back().is_some_and(|c|c.is_alphanumeric())
    }).map(|m|m.as_str()).take(97).collect();
    if tokens.is_empty() || tokens.len()>96 || tokens.iter().filter(|t|words(t).next().is_some()).count()>32 { return None; }
    let mut parser=Parser{tokens,index:0,explicit:false,any:mode==ContentMatchMode::AnyWord};
    let root=parser.or(0)?;
    (parser.index==parser.tokens.len()).then_some((root,parser.explicit))
}

const STOP_WORDS: &str = "a an the and or of to in on for with about from by at as is are was were be been that this these those it its my your our their file files document documents looking find search show me please something where what when how into onto over under between through during before after above below up down out off all any both each few more most other some such only own same so than too very can will just should now de het een van voor met over naar uit dat die dit mijn bestand bestanden zoek zoeken vind geef toon als ook graag en of ik je";

pub(crate) struct SmartQuery {
    pub groups: Vec<Node>,
    pub excluded: Vec<Node>,
    pub inferred: Vec<String>,
}

impl SmartQuery {
    pub fn root(&self) -> Node {
        let include = self.groups.iter().cloned().reduce(|a, b| a.join(b, true)).unwrap();
        self.excluded.iter().cloned().fold(include, |root, exclusion| {
            root.join(Node::Not(Box::new(exclusion)), false)
        })
    }
}

// None delegates to the Boolean parser. Some(None) is an invalid natural query.
pub(crate) fn smart(input: &str) -> Option<Option<SmartQuery>> {
    if input.encode_utf16().count() > 4096 { return Some(None); }
    static TOKENS: OnceLock<regex::Regex> = OnceLock::new();
    let re = TOKENS.get_or_init(|| regex::Regex::new(
        r#""[^"]*"|"|&&|\|\|?|[()/\-]|[\p{L}\p{N}][\p{L}\p{M}\p{N}]*\*?"#
    ).unwrap());
    let tokens: Vec<_> = re.find_iter(input).filter(|m| {
        match m.as_str() {
            "-" => !input[..m.start()].chars().next_back().is_some_and(char::is_alphanumeric),
            "/" => input[..m.start()].ends_with(|c: char| c.is_alphanumeric() || c == '*' || c == '"')
                && input[m.end()..].starts_with(|c: char| c.is_alphanumeric() || c == '"'),
            _ => true,
        }
    }).map(|m| m.as_str()).take(97).collect();
    if tokens.iter().any(|token| matches!(*token, "AND" | "OR" | "NOT" | "(" | ")" | "&&" | "|" | "||")) {
        return None;
    }
    Some(natural_query(&tokens))
}

fn natural_query(tokens: &[&str]) -> Option<SmartQuery> {
    if tokens.len() > 96 || tokens.iter().filter(|t| words(t).next().is_some()).count() > 32 {
        return None;
    }
    let mut tokens = tokens.iter().copied().peekable();
    let mut query = SmartQuery { groups: Vec::new(), excluded: Vec::new(), inferred: Vec::new() };
    while let Some(token) = tokens.next() {
        let inferred = matches!(token.to_ascii_lowercase().as_str(),
            "without" | "except" | "excluding" | "exclude" | "not" | "zonder" | "behalve" | "niet")
            && tokens.peek().is_some_and(|next| words(next).next().is_some());
        let excluded = inferred || token == "-";
        let token = if excluded { tokens.next()? } else { token };
        let mut group = natural_term(token)?;
        let mut alternatives = vec![token.trim_matches('"')];
        while tokens.next_if_eq(&"/").is_some() {
            let alternative = tokens.next()?;
            group = group.join(natural_term(alternative)?, true);
            alternatives.push(alternative.trim_matches('"'));
        }
        if excluded {
            query.excluded.push(group);
            if inferred { query.inferred.push(alternatives.join(" / ")); }
        } else if !query.groups.contains(&group) {
            query.groups.push(group);
        }
    }
    let content_group = |group: &Node| !matches!(group, Node::Term(term)
        if !term.prefix && !term.phrase && STOP_WORDS.split_ascii_whitespace()
            .any(|word| word.eq_ignore_ascii_case(&term.text)));
    if query.groups.iter().filter(|group| content_group(group)).count() >= 2 {
        query.groups.retain(content_group);
    }
    (!query.groups.is_empty()).then_some(query)
}

fn natural_term(token: &str) -> Option<Node> {
    let phrase = token.starts_with('"');
    let prefix = token.ends_with('*');
    let text = words(token).collect::<Vec<_>>().join(" ");
    (!text.is_empty()).then_some(Node::Term(Term {
        text: text.to_lowercase(), prefix, phrase, verbatim: phrase,
    }))
}

struct Parser<'a> { tokens:Vec<&'a str>,index:usize,explicit:bool,any:bool }
impl<'a> Parser<'a> {
    fn current(&self)->&'a str { self.tokens.get(self.index).copied().unwrap_or("") }
    fn operand_starts(&self)->bool {
        !matches!(self.current().to_ascii_uppercase().as_str(),""|")"|"AND"|"&&"|"OR"|"|"|"||")
    }
    fn or(&mut self,depth:u8)->Option<Node> {
        let mut left=self.adjacent(depth)?;
        while matches!(self.current().to_ascii_uppercase().as_str(),"OR"|"|"|"||") {
            self.explicit=true;
            self.index+=1;
            left=left.join(self.adjacent(depth)?,true);
        }
        Some(left)
    }
    fn adjacent(&mut self,depth:u8)->Option<Node> {
        let first=self.and(depth)?;
        if !self.any || !self.operand_starts() { return Some(first); }
        let (mut include,mut exclude)=(None,None);
        let mut current=Some(first);
        while let Some(node)=current {
            let (slot,node)=match node { Node::Not(node)=>(&mut exclude,*node), _=>(&mut include,node) };
            *slot=Some(match slot.take() { Some(previous)=>Node::join(previous,node,true),None=>node });
            current=if self.operand_starts() { Some(self.and(depth)?) } else { None };
        }
        match (include,exclude) {
            (Some(a),Some(b))=>Some(a.join(Node::Not(Box::new(b)),false)),
            (None,Some(b))=>Some(Node::Not(Box::new(b))),
            (a,None)=>a,
        }
    }
    fn and(&mut self,depth:u8)->Option<Node> {
        let mut left=self.operand(depth)?;
        loop {
            let explicit=matches!(self.current().to_ascii_uppercase().as_str(),"AND"|"&&");
            if !explicit && (self.any || !self.operand_starts()) { break; }
            if explicit { self.explicit=true; self.index+=1; }
            left=left.join(self.operand(depth)?,false);
        }
        Some(left)
    }
    fn operand(&mut self,depth:u8)->Option<Node> {
        if depth>=12 || !self.operand_starts() { return None; }
        let token=self.current();
        self.index+=1;
        match token.to_ascii_uppercase().as_str() {
            "NOT"|"-"=>{
                self.explicit|=token!="-";
                Some(Node::Not(Box::new(self.operand(depth+1)?)))
            }
            "("=>{
                self.explicit=true;
                let group=self.or(depth+1)?;
                if self.current()!=")" { return None; }
                self.index+=1;
                Some(group)
            }
            "\"" =>None,
            _=>{
                let phrase=token.starts_with('"');
                let prefix=token.ends_with('*');
                let text=if phrase { words(token).collect::<Vec<_>>().join(" ") }
                    else { token.trim_end_matches('*').to_owned() };
                (!text.is_empty()).then_some(Node::Term(Term{text,prefix,phrase,verbatim:false}))
            }
        }
    }
}

pub(crate) fn distance(a: &str, b: &str) -> usize {
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
                + (i - previous_row - 1) + 1 + (j - transposed_column - 1);
            scores[cell] = substitution.min(insertion).min(deletion).min(transposition);
        }
        previous_rows.insert(left, i);
    }
    scores[scores.len() - 1]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn edit_distance_counts_transpositions_and_insertions() {
        assert_eq!(distance("CA", "ABC"), 2);
        assert_eq!(distance("reciept", "receipt"), 1);
        assert_eq!(distance("niovce", "invoice"), 3);
        assert_eq!(distance("café", "cafe"), 1);
        assert_eq!(distance("", "abc"), 3);
    }
}
