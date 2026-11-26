use std::{
    cmp::Reverse,
    collections::{HashMap, HashSet},
    hash::{BuildHasherDefault, Hasher},
    sync::{Arc, Mutex},
};

use rusqlite::Connection;

pub const PREFIX_LEN: usize = 8;

const MAX_WORD_CHARS: usize = 64;
const SNIPPET_SCAN_BYTES: usize = 96 * 1024;
const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;
const FX_SEED: u64 = 0x517c_c1b7_2722_0a95;

#[derive(Default)]
struct FxHasher(u64);

impl Hasher for FxHasher {
    #[inline]
    fn finish(&self) -> u64 {
        self.0
    }

    #[inline]
    fn write(&mut self, bytes: &[u8]) {
        let mut h = self.0;
        for &b in bytes {
            h = (h.rotate_left(5) ^ u64::from(b)).wrapping_mul(FX_SEED);
        }
        self.0 = h;
    }

    #[inline]
    fn write_u32(&mut self, i: u32) {
        self.0 = (self.0.rotate_left(5) ^ u64::from(i)).wrapping_mul(FX_SEED);
    }

    #[inline]
    fn write_u64(&mut self, i: u64) {
        self.0 = (self.0.rotate_left(5) ^ i).wrapping_mul(FX_SEED);
    }
}

type FastHashBuilder = BuildHasherDefault<FxHasher>;
type DeleteMap = HashMap<u64, Vec<u32>, FastHashBuilder>;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VocabEntry {
    pub term: String,
    pub doc_freq: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FuzzyMatch {
    pub term: String,
    pub distance: usize,
    pub doc_freq: u64,
}

#[derive(Clone, Debug, Default)]
pub struct SymSpellIndex {
    entries: Vec<VocabEntry>,
    deletes: DeleteMap,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct HighlightedSnippet {
    pub text: String,
    pub matched_terms: Vec<String>,
}

type VocabCacheTuple = (String, u64, i64, Arc<SymSpellIndex>);

static VOCAB_CACHE: Mutex<Option<VocabCacheTuple>> = Mutex::new(None);

/// Thread-safe process-wide cache of the FTS5 vocabulary SymSpell index.
/// Rebuilds `temp.content_vocab` and the in-memory delete map ONLY when
/// `(db_key, indexed_files, latest_indexed_at)` changes.
pub fn cached_vocab(
    db: &Connection,
    db_key: &str,
    indexed_files: u64,
    latest_indexed_at: i64,
) -> Arc<SymSpellIndex> {
    {
        let guard = VOCAB_CACHE.lock().unwrap_or_else(|e| e.into_inner());
        if let Some((ref key, files, latest, ref index)) = *guard {
            if key == db_key && files == indexed_files && latest == latest_indexed_at {
                return Arc::clone(index);
            }
        }
    }

    if indexed_files == 0 {
        let empty = Arc::new(SymSpellIndex::default());
        let mut guard = VOCAB_CACHE.lock().unwrap_or_else(|e| e.into_inner());
        *guard = Some((
            db_key.to_owned(),
            indexed_files,
            latest_indexed_at,
            Arc::clone(&empty),
        ));
        return empty;
    }

    let loaded = load_vocab_from_db(db);
    match loaded {
        Ok(entries) => {
            let built = Arc::new(SymSpellIndex::from_vocab(entries));
            let mut guard = VOCAB_CACHE.lock().unwrap_or_else(|e| e.into_inner());
            *guard = Some((
                db_key.to_owned(),
                indexed_files,
                latest_indexed_at,
                Arc::clone(&built),
            ));
            built
        }
        Err(_) => Arc::new(SymSpellIndex::default()),
    }
}

fn load_vocab_from_db(db: &Connection) -> rusqlite::Result<Vec<(String, u64)>> {
    db.execute_batch(
        "CREATE VIRTUAL TABLE IF NOT EXISTS temp.content_vocab USING fts5vocab(main, content_fts, row);",
    )?;
    let mut stmt = db.prepare("SELECT term, doc FROM temp.content_vocab")?;
    let mut rows = stmt.query([])?;
    let mut out = Vec::new();
    while let Some(row) = rows.next()? {
        let term: String = row.get(0)?;
        let doc: i64 = row.get(1)?;
        if doc > 0 {
            out.push((term, doc as u64));
        }
    }
    Ok(out)
}

impl SymSpellIndex {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn entries(&self) -> &[VocabEntry] {
        &self.entries
    }

    pub fn from_vocab(iter: impl IntoIterator<Item = (String, u64)>) -> Self {
        let mut raw_entries: Vec<VocabEntry> = Vec::new();
        let mut exact_dedup: HashMap<String, usize, FastHashBuilder> = HashMap::default();

        for (raw_term, doc_freq) in iter {
            if doc_freq == 0 {
                continue;
            }
            let term = fold_diacritics(&raw_term);
            let char_len = term.chars().count();
            if !(2..=MAX_WORD_CHARS).contains(&char_len) || term.chars().all(char::is_numeric) {
                continue;
            }
            if let Some(&idx) = exact_dedup.get(&term) {
                raw_entries[idx].doc_freq = raw_entries[idx].doc_freq.saturating_add(doc_freq);
            } else {
                if raw_entries.len() >= u32::MAX as usize {
                    break;
                }
                let idx = raw_entries.len();
                exact_dedup.insert(term.clone(), idx);
                raw_entries.push(VocabEntry { term, doc_freq });
            }
        }

        let mut deletes: DeleteMap =
            HashMap::with_capacity_and_hasher(raw_entries.len().saturating_mul(8), FastHashBuilder::default());
        let mut prefix_buf = ['\0'; PREFIX_LEN];
        let mut hash_buf = [0u64; 40];

        for (idx, entry) in raw_entries.iter().enumerate() {
            let plen = fill_prefix(&entry.term, &mut prefix_buf);
            let hcount = collect_delete_hashes(&prefix_buf[..plen], 2, &mut hash_buf);
            let idx_u32 = idx as u32;
            for &h in &hash_buf[..hcount] {
                deletes.entry(h).or_default().push(idx_u32);
            }
        }

        Self {
            entries: raw_entries,
            deletes,
        }
    }

    /// Look up the exact folded document frequency of `term` in O(1) time.
    pub fn doc_freq(&self, term: &str) -> u64 {
        let folded = fold_diacritics(term);
        let mut prefix_buf = ['\0'; PREFIX_LEN];
        let plen = fill_prefix(&folded, &mut prefix_buf);
        if plen < 2 {
            return 0;
        }
        let h = hash_prefix_deletes(&prefix_buf[..plen], usize::MAX, usize::MAX);
        let Some(bucket) = self.deletes.get(&h) else {
            return 0;
        };
        for &idx in bucket {
            let entry = &self.entries[idx as usize];
            if entry.term == folded {
                return entry.doc_freq;
            }
        }
        0
    }

    /// Suggest typo/stem corrections for `raw_word`.
    ///
    /// - Skips if `exact_doc_freq >= 5`.
    /// - When `1 <= exact_doc_freq < 5`, returns matches ONLY if at least one 1-edit
    ///   candidate has `doc_freq >= exact_doc_freq.saturating_mul(8).max(5)`.
    pub fn suggest(&self, raw_word: &str, exact_doc_freq: u64, limit: usize) -> Vec<FuzzyMatch> {
        if limit == 0 || exact_doc_freq >= 5 || self.entries.is_empty() {
            return Vec::new();
        }

        let trimmed = raw_word.trim();
        if trimmed.is_empty() {
            return Vec::new();
        }
        let raw_lower = trimmed.to_lowercase();
        let folded = fold_diacritics(trimmed);
        let folded_chars: Vec<char> = folded.chars().collect();
        let folded_len = folded_chars.len();
        if !(3..=MAX_WORD_CHARS).contains(&folded_len) || folded_chars.iter().all(|c| c.is_numeric()) {
            return Vec::new();
        }

        let max_dist: usize = if folded_len <= 5 { 1 } else { 2 };
        let probes = stem_probes(&folded);

        // Initial-transposition probe (e.g. `niovce` -> `inovce` -> `invoic`) for 6+ char words,
        // preserving full typo parity with v1.5.6 when initial swap pairs with an inner typo.
        let repaired_info = if folded_len >= 6 && folded_chars[0] != folded_chars[1] {
            let swapped_two: String = [folded_chars[1], folded_chars[0]].into_iter().collect();
            let rest: String = folded_chars[2..].iter().collect();
            let repaired_word = format!("{swapped_two}{rest}");
            let repaired_probes = stem_probes(&repaired_word);
            Some((swapped_two, repaired_probes))
        } else {
            None
        };

        let mut candidate_ids: HashSet<u32, FastHashBuilder> = HashSet::default();
        let mut prefix_buf = ['\0'; PREFIX_LEN];
        let mut hash_buf = [0u64; 40];

        for probe in &probes {
            let plen = fill_prefix(probe, &mut prefix_buf);
            let hcount = collect_delete_hashes(&prefix_buf[..plen], max_dist, &mut hash_buf);
            for &h in &hash_buf[..hcount] {
                if let Some(bucket) = self.deletes.get(&h) {
                    candidate_ids.extend(bucket.iter().copied());
                }
            }
        }

        if let Some((_, ref rep_probes)) = repaired_info {
            for probe in rep_probes {
                let plen = fill_prefix(probe, &mut prefix_buf);
                let hcount = collect_delete_hashes(&prefix_buf[..plen], max_dist, &mut hash_buf);
                for &h in &hash_buf[..hcount] {
                    if let Some(bucket) = self.deletes.get(&h) {
                        candidate_ids.extend(bucket.iter().copied());
                    }
                }
            }
        }

        if candidate_ids.is_empty() {
            return Vec::new();
        }

        let min_override_freq = if exact_doc_freq > 0 {
            exact_doc_freq.saturating_mul(8).max(5)
        } else {
            0
        };

        let first_char = folded_chars[0];
        let mut ranked: Vec<((usize, Reverse<bool>, Reverse<usize>, Reverse<u64>), usize)> = Vec::new();

        for idx in candidate_ids {
            let entry = &self.entries[idx as usize];
            if exact_doc_freq > 0 && entry.doc_freq < min_override_freq {
                continue;
            }

            let cand = &entry.term;
            let cand_len = cand.chars().count();
            if cand_len.abs_diff(folded_len) > max_dist + 3 {
                continue;
            }

            let mut best_dist = usize::MAX;
            let mut zero_probe_hit = false;

            for probe in &probes {
                let d = probe_distance(probe, cand, max_dist);
                if d == 0 {
                    zero_probe_hit = true;
                }
                if d < best_dist {
                    best_dist = d;
                }
            }

            // Exclude 0-distance matches that merely echo the already-matched FTS stem or literal ASCII input.
            if zero_probe_hit && (exact_doc_freq > 0 || *cand == raw_lower) {
                continue;
            }

            if best_dist > max_dist {
                if let Some((ref swapped_two, ref rep_probes)) = repaired_info {
                    if cand.starts_with(swapped_two.as_str()) {
                        let mut rep_best = usize::MAX;
                        for rep in rep_probes {
                            rep_best = rep_best.min(probe_distance(rep, cand, max_dist));
                        }
                        if rep_best <= max_dist {
                            best_dist = max_dist;
                        }
                    }
                }
            }

            if best_dist <= max_dist {
                let same_first = cand.chars().next() == Some(first_char);
                let common_prefix = folded_chars
                    .iter()
                    .copied()
                    .zip(cand.chars())
                    .take_while(|(a, b)| a == b)
                    .count();
                ranked.push((
                    (
                        best_dist,
                        Reverse(same_first),
                        Reverse(common_prefix),
                        Reverse(entry.doc_freq),
                    ),
                    idx as usize,
                ));
            }
        }

        if exact_doc_freq > 0 {
            let has_strong_one_edit = ranked.iter().any(|((dist, _, _, _), idx)| {
                *dist == 1 && self.entries[*idx].doc_freq >= min_override_freq
            });
            if !has_strong_one_edit {
                return Vec::new();
            }
        }

        ranked.sort_unstable_by(|(ka, ia), (kb, ib)| {
            ka.cmp(kb)
                .then_with(|| self.entries[*ia].term.cmp(&self.entries[*ib].term))
        });
        ranked.truncate(limit);

        ranked
            .into_iter()
            .map(|((distance, _, _, _), idx)| {
                let entry = &self.entries[idx];
                FuzzyMatch {
                    term: entry.term.clone(),
                    distance,
                    doc_freq: entry.doc_freq,
                }
            })
            .collect()
    }
}

/// Zero-dependency lowercase + European/Dutch/French/German diacritic folder.
pub fn fold_diacritics(s: &str) -> String {
    if s.is_ascii() {
        return s.to_ascii_lowercase();
    }
    let mut out = String::with_capacity(s.len());
    for raw_ch in s.chars() {
        if is_combining_mark(raw_ch) {
            continue;
        }
        if raw_ch == 'ß' || raw_ch == 'ẞ' {
            out.push_str("ss");
            continue;
        }
        for ch in raw_ch.to_lowercase() {
            if is_combining_mark(ch) {
                continue;
            }
            match ch {
                'á' | 'à' | 'â' | 'ä' | 'ã' | 'å' | 'ā' | 'ă' | 'ą' => out.push('a'),
                'æ' => out.push_str("ae"),
                'ç' | 'ć' | 'č' | 'ĉ' | 'ċ' => out.push('c'),
                'ď' | 'đ' | 'ð' => out.push('d'),
                'é' | 'è' | 'ê' | 'ë' | 'ē' | 'ĕ' | 'ė' | 'ę' | 'ě' => out.push('e'),
                'ğ' | 'ĝ' | 'ġ' | 'ģ' => out.push('g'),
                'ĥ' | 'ħ' => out.push('h'),
                'í' | 'ì' | 'î' | 'ï' | 'ĩ' | 'ī' | 'ĭ' | 'į' | 'ı' => out.push('i'),
                'ĳ' => out.push_str("ij"),
                'ĵ' => out.push('j'),
                'ķ' => out.push('k'),
                'ĺ' | 'ļ' | 'ľ' | 'ł' => out.push('l'),
                'ñ' | 'ń' | 'ņ' | 'ň' => out.push('n'),
                'ó' | 'ò' | 'ô' | 'ö' | 'õ' | 'ø' | 'ō' | 'ŏ' | 'ő' => out.push('o'),
                'œ' => out.push_str("oe"),
                'ŕ' | 'ŗ' | 'ř' => out.push('r'),
                'ś' | 'ŝ' | 'ş' | 'š' => out.push('s'),
                'ß' => out.push_str("ss"),
                'ť' | 'ţ' | 'ŧ' => out.push('t'),
                'þ' => out.push_str("th"),
                'ú' | 'ù' | 'û' | 'ü' | 'ũ' | 'ū' | 'ŭ' | 'ů' | 'ű' | 'ų' => out.push('u'),
                'ŵ' => out.push('w'),
                'ý' | 'ÿ' | 'ŷ' => out.push('y'),
                'ź' | 'ż' | 'ž' => out.push('z'),
                other => out.push(other),
            }
        }
    }
    out
}

#[inline]
fn is_combining_mark(ch: char) -> bool {
    matches!(
        ch,
        '\u{0300}'..='\u{036f}'
            | '\u{1ab0}'..='\u{1aff}'
            | '\u{1dc0}'..='\u{1dff}'
            | '\u{20d0}'..='\u{20ff}'
            | '\u{fe20}'..='\u{fe2f}'
    )
}

#[inline]
fn is_word_continue(ch: char) -> bool {
    ch.is_alphanumeric() || is_combining_mark(ch)
}

fn fill_prefix(s: &str, out: &mut [char; PREFIX_LEN]) -> usize {
    let mut len = 0;
    for ch in s.chars().take(PREFIX_LEN) {
        out[len] = ch;
        len += 1;
    }
    len
}

#[inline]
fn hash_prefix_deletes(prefix: &[char], skip1: usize, skip2: usize) -> u64 {
    let mut h = FNV_OFFSET;
    for (i, &ch) in prefix.iter().enumerate() {
        if i != skip1 && i != skip2 {
            let code = ch as u32;
            if code < 0x80 {
                h ^= u64::from(code);
                h = h.wrapping_mul(FNV_PRIME);
            } else {
                for b in code.to_le_bytes() {
                    h ^= u64::from(b);
                    h = h.wrapping_mul(FNV_PRIME);
                }
            }
        }
    }
    h ^= h >> 33;
    h = h.wrapping_mul(0xff51_afd7_ed55_8ccd);
    h ^= h >> 33;
    h
}

fn collect_delete_hashes(prefix: &[char], max_deletes: usize, out: &mut [u64; 40]) -> usize {
    let mut count = 0usize;
    let mut push_unique = |h: u64| {
        if !out[..count].contains(&h) {
            out[count] = h;
            count += 1;
        }
    };

    let plen = prefix.len();
    if plen >= 2 {
        push_unique(hash_prefix_deletes(prefix, usize::MAX, usize::MAX));
    }
    if max_deletes >= 1 && plen >= 3 {
        for i in 0..plen {
            push_unique(hash_prefix_deletes(prefix, i, usize::MAX));
        }
    }
    if max_deletes >= 2 && plen >= 4 {
        for i in 0..plen {
            for j in (i + 1)..plen {
                push_unique(hash_prefix_deletes(prefix, i, j));
            }
        }
    }
    count
}

fn stem_probes(folded: &str) -> Vec<String> {
    let mut probes = Vec::with_capacity(3);
    probes.push(folded.to_owned());
    for suffix in ["ing", "ed", "es", "s", "e"] {
        if let Some(stripped) = folded.strip_suffix(suffix) {
            if stripped.chars().count() >= 3 && !probes.iter().any(|p| p == stripped) {
                probes.push(stripped.to_owned());
            }
        }
    }
    probes
}

fn canonical_stem(folded: &str) -> &str {
    for suffix in ["ing", "ed", "es", "s", "e"] {
        if let Some(stripped) = folded.strip_suffix(suffix) {
            if stripped.chars().count() >= 3 {
                return stripped;
            }
        }
    }
    folded
}

fn probe_distance(probe: &str, candidate: &str, max_dist: usize) -> usize {
    let mut d = damerau_levenshtein(probe, candidate, max_dist);
    if d > 1 && probe.ends_with('e') && !candidate.ends_with('e') {
        let mut with_e = String::with_capacity(candidate.len() + 1);
        with_e.push_str(candidate);
        with_e.push('e');
        d = d.min(damerau_levenshtein(probe, &with_e, max_dist));
    }
    d
}

/// True unrestricted Damerau-Levenshtein edit distance (adjacent transpositions cost 1).
/// Uses a zero-allocation stack matrix for words up to 64 characters.
pub fn damerau_levenshtein(a: &str, b: &str, max_dist: usize) -> usize {
    if a == b {
        return 0;
    }
    let mut a_buf = ['\0'; MAX_WORD_CHARS + 2];
    let mut b_buf = ['\0'; MAX_WORD_CHARS + 2];
    let mut a_len = 0usize;
    for ch in a.chars() {
        if a_len >= a_buf.len() {
            return damerau_levenshtein_heap(a, b);
        }
        a_buf[a_len] = ch;
        a_len += 1;
    }
    let mut b_len = 0usize;
    for ch in b.chars() {
        if b_len >= b_buf.len() {
            return damerau_levenshtein_heap(a, b);
        }
        b_buf[b_len] = ch;
        b_len += 1;
    }

    if a_len.abs_diff(b_len) > max_dist {
        return max_dist + 1;
    }
    if a_len == 0 {
        return b_len;
    }
    if b_len == 0 {
        return a_len;
    }

    let a_slice = &a_buf[..a_len];
    let b_slice = &b_buf[..b_len];
    if a_len <= 16 && b_len <= 16 {
        dl_stack::<SMALL_DL_CELLS>(a_slice, b_slice)
    } else {
        dl_stack::<LARGE_DL_CELLS>(a_slice, b_slice)
    }
}

const SMALL_DL_CELLS: usize = 18 * 18;
const LARGE_DL_CELLS: usize = (MAX_WORD_CHARS + 4) * (MAX_WORD_CHARS + 4);

fn dl_stack<const CELLS: usize>(a_slice: &[char], b_slice: &[char]) -> usize {
    let a_len = a_slice.len();
    let b_len = b_slice.len();
    let width = b_len + 2;
    let inf = (a_len + b_len) as u16;
    let mut scores = [0u16; CELLS];

    scores[0] = inf;
    for i in 0..=a_len {
        scores[(i + 1) * width] = inf;
        scores[(i + 1) * width + 1] = i as u16;
    }
    for j in 0..=b_len {
        scores[j] = inf;
        scores[width + j + 1] = j as u16;
    }

    for i in 1..=a_len {
        let left = a_slice[i - 1];
        let mut prev_col = 0usize;
        for j in 1..=b_len {
            let right = b_slice[j - 1];
            let same = left == right;
            let mut prev_row = 0usize;
            for k in (1..i).rev() {
                if a_slice[k - 1] == right {
                    prev_row = k;
                    break;
                }
            }
            let trans_col = prev_col;
            if same {
                prev_col = j;
            }
            let cell = (i + 1) * width + j + 1;
            let sub = scores[cell - width - 1] + u16::from(!same);
            let ins = scores[cell - 1] + 1;
            let del = scores[cell - width] + 1;
            let trans = scores[prev_row * width + trans_col]
                + ((i - prev_row - 1) + 1 + (j - trans_col - 1)) as u16;
            scores[cell] = sub.min(ins).min(del).min(trans);
        }
    }

    scores[(a_len + 1) * width + b_len + 1] as usize
}

fn damerau_levenshtein_heap(a: &str, b: &str) -> usize {
    let a: Vec<char> = a.chars().collect();
    let b: Vec<char> = b.chars().collect();
    let width = b.len() + 2;
    let inf = a.len() + b.len();
    let mut scores = vec![inf; (a.len() + 2) * width];
    for i in 0..=a.len() {
        scores[(i + 1) * width + 1] = i;
    }
    for j in 0..=b.len() {
        scores[width + j + 1] = j;
    }
    let mut prev_rows: HashMap<char, usize, FastHashBuilder> = HashMap::default();
    for (i, &left) in (1..).zip(&a) {
        let mut prev_col = 0usize;
        for (j, &right) in (1..).zip(&b) {
            let same = left == right;
            let prev_row = prev_rows.get(&right).copied().unwrap_or(0);
            let trans_col = prev_col;
            if same {
                prev_col = j;
            }
            let cell = (i + 1) * width + j + 1;
            let sub = scores[cell - width - 1] + usize::from(!same);
            let ins = scores[cell - 1] + 1;
            let del = scores[cell - width] + 1;
            let trans = scores[prev_row * width + trans_col]
                + (i - prev_row - 1)
                + 1
                + (j - trans_col - 1);
            scores[cell] = sub.min(ins).min(del).min(trans);
        }
        prev_rows.insert(left, i);
    }
    scores[scores.len() - 1]
}

struct PreparedQueryTerm {
    label: String,
    folded: String,
    folded_chars: usize,
    stem: String,
    stem_chars: usize,
}

#[derive(Clone, Copy)]
struct WordHit {
    start: usize,
    end: usize,
    mask: u64,
}

/// Single-pass diacritic-folding, stem-aware snippet extractor & `«word»` match highlighter.
pub fn extract_snippet(
    body: &str,
    query_terms: &[String],
    width: usize,
    mark_highlights: bool,
) -> HighlightedSnippet {
    if body.trim().is_empty() {
        return HighlightedSnippet::default();
    }
    let width = if width == 0 { 240 } else { width };
    let scan_end = floor_char_boundary(body, SNIPPET_SCAN_BYTES);
    let scan_body = &body[..scan_end];

    let prepared = prepare_query_terms(query_terms);
    if prepared.is_empty() {
        let end = snap_window_end(scan_body, floor_char_boundary(scan_body, width), 0);
        let text = render_collapsed_window(scan_body, 0, end, &[], false, end < body.len());
        return HighlightedSnippet {
            text,
            matched_terms: Vec::new(),
        };
    }

    let mut hits: Vec<WordHit> = Vec::new();
    let mut iter = scan_body.char_indices().peekable();

    while let Some(&(start, ch)) = iter.peek() {
        if !ch.is_alphanumeric() {
            iter.next();
            continue;
        }
        let mut end = start + ch.len_utf8();
        iter.next();
        while let Some(&(next_idx, next_ch)) = iter.peek() {
            if is_word_continue(next_ch) {
                end = next_idx + next_ch.len_utf8();
                iter.next();
            } else {
                break;
            }
        }

        let word = &scan_body[start..end];
        let folded_w = fold_diacritics(word);
        let stem_w = canonical_stem(&folded_w);

        let mut mask = 0u64;
        for (idx, qt) in prepared.iter().enumerate() {
            if word_matches_term(&folded_w, stem_w, qt) {
                mask |= 1u64 << idx;
            }
        }
        if mask != 0 {
            hits.push(WordHit { start, end, mask });
        }
        if hits.len() >= 16_384 {
            break;
        }
    }

    if hits.is_empty() {
        let end = snap_window_end(scan_body, floor_char_boundary(scan_body, width), 0);
        let text = render_collapsed_window(scan_body, 0, end, &[], false, end < body.len());
        return HighlightedSnippet {
            text,
            matched_terms: Vec::new(),
        };
    }

    let (win_start, win_end) = if scan_body.len() <= width {
        (0, scan_body.len())
    } else {
        choose_best_window(scan_body, &hits, width)
    };

    let first_hit = hits.partition_point(|h| h.start < win_start);
    let last_hit = hits.partition_point(|h| h.end <= win_end);
    let window_hits = if first_hit <= last_hit {
        &hits[first_hit..last_hit]
    } else {
        &[][..]
    };

    let mut union_mask = 0u64;
    for h in window_hits {
        union_mask |= h.mask;
    }
    let matched_terms: Vec<String> = prepared
        .iter()
        .enumerate()
        .filter(|(idx, _)| (union_mask & (1u64 << idx)) != 0)
        .map(|(_, qt)| qt.label.clone())
        .collect();

    let text = render_collapsed_window(
        scan_body,
        win_start,
        win_end,
        window_hits,
        mark_highlights,
        win_end < body.len(),
    );

    HighlightedSnippet {
        text,
        matched_terms,
    }
}

fn prepare_query_terms(query_terms: &[String]) -> Vec<PreparedQueryTerm> {
    let mut out: Vec<PreparedQueryTerm> = Vec::new();
    for raw in query_terms {
        if out.len() >= 64 {
            break;
        }
        let trimmed = raw.trim();
        if trimmed.is_empty() {
            continue;
        }
        let folded = fold_diacritics(trimmed);
        if folded.is_empty() || out.iter().any(|p| p.folded == folded) {
            continue;
        }
        let folded_chars = folded.chars().count();
        let stem = canonical_stem(&folded).to_owned();
        let stem_chars = stem.chars().count();
        out.push(PreparedQueryTerm {
            label: trimmed.to_lowercase(),
            folded,
            folded_chars,
            stem,
            stem_chars,
        });
    }
    out
}

#[inline]
fn word_matches_term(folded_w: &str, stem_w: &str, qt: &PreparedQueryTerm) -> bool {
    if folded_w == qt.folded {
        return true;
    }
    if qt.stem_chars >= 3 && stem_w == qt.stem {
        return true;
    }
    if qt.folded_chars >= 3 && folded_w.starts_with(&qt.folded) {
        return true;
    }
    qt.stem_chars >= 4 && folded_w.starts_with(&qt.stem)
}

fn choose_best_window(scan_body: &str, hits: &[WordHit], width: usize) -> (usize, usize) {
    let span_budget = width.saturating_sub(24).max(width / 2);
    let mut counts = [0u32; 64];
    let mut distinct = 0usize;
    let mut left = 0usize;

    let mut best_score = (0usize, 0usize, Reverse(usize::MAX));
    let mut best_bounds = (0usize, floor_char_boundary(scan_body, width));

    for (right, &rhit) in hits.iter().enumerate() {
        add_mask(&mut counts, &mut distinct, rhit.mask);

        while left < right && rhit.end.saturating_sub(hits[left].start) > span_budget {
            remove_mask(&mut counts, &mut distinct, hits[left].mask);
            left += 1;
        }

        let total_hits = right - left + 1;
        let first_start = hits[left].start;
        let last_end = rhit.end;

        let raw_start = if last_end.saturating_sub(first_start) >= width {
            first_start
        } else {
            let mid = first_start + (last_end - first_start) / 2;
            mid.saturating_sub(width / 2)
                .min(scan_body.len().saturating_sub(width))
                .min(first_start)
        };

        let score = (distinct, total_hits, Reverse(raw_start));
        if score > best_score {
            best_score = score;
            let mut win_start = ceil_char_boundary(scan_body, raw_start);
            let min_end = ceil_char_boundary(scan_body, win_start.saturating_add(1));
            let mut win_end = floor_char_boundary(scan_body, win_start.saturating_add(width)).max(min_end);
            if win_end < last_end && last_end <= win_start.saturating_add(width) {
                win_end = last_end;
            }
            win_start = snap_window_start(scan_body, win_start, first_start);
            win_end = snap_window_end(scan_body, win_end, last_end.min(win_end));
            best_bounds = (win_start, win_end);
        }
    }

    best_bounds
}

#[inline]
fn add_mask(counts: &mut [u32; 64], distinct: &mut usize, mut mask: u64) {
    while mask != 0 {
        let bit = mask.trailing_zeros() as usize;
        if counts[bit] == 0 {
            *distinct += 1;
        }
        counts[bit] += 1;
        mask &= mask - 1;
    }
}

#[inline]
fn remove_mask(counts: &mut [u32; 64], distinct: &mut usize, mut mask: u64) {
    while mask != 0 {
        let bit = mask.trailing_zeros() as usize;
        counts[bit] -= 1;
        if counts[bit] == 0 {
            *distinct -= 1;
        }
        mask &= mask - 1;
    }
}

fn snap_window_start(s: &str, start: usize, must_keep_before_or_at: usize) -> usize {
    if start == 0 || start >= s.len() {
        return start;
    }
    let prev_char = s[..start].chars().next_back();
    let curr_char = s[start..].chars().next();
    if !prev_char.is_some_and(is_word_continue) || !curr_char.is_some_and(is_word_continue) {
        return start;
    }
    // Avoid slicing mid-word: advance to the next whitespace within 18 bytes if it keeps our first hit intact.
    let limit = floor_char_boundary(s, start.saturating_add(18).min(must_keep_before_or_at));
    if limit <= start {
        return start;
    }
    for (offset, ch) in s[start..limit].char_indices() {
        if ch.is_whitespace() {
            return start + offset + ch.len_utf8();
        }
    }
    start
}

fn snap_window_end(s: &str, end: usize, must_keep_after_or_at: usize) -> usize {
    if end >= s.len() || end == 0 {
        return end.min(s.len());
    }
    let prev_char = s[..end].chars().next_back();
    let curr_char = s[end..].chars().next();
    if !prev_char.is_some_and(is_word_continue) || !curr_char.is_some_and(is_word_continue) {
        return end;
    }
    // Avoid slicing mid-word: step back to previous whitespace within 18 bytes if it keeps our last hit intact.
    let floor = ceil_char_boundary(s, end.saturating_sub(18).max(must_keep_after_or_at));
    if floor >= end {
        return end;
    }
    for (offset, ch) in s[floor..end].char_indices().rev() {
        if ch.is_whitespace() {
            return floor + offset;
        }
    }
    end
}

fn render_collapsed_window(
    scan_body: &str,
    win_start: usize,
    win_end: usize,
    hits: &[WordHit],
    mark_highlights: bool,
    has_more_after: bool,
) -> String {
    let mut core = String::with_capacity(win_end.saturating_sub(win_start) + hits.len() * 4);
    let mut cursor = win_start;
    let mut pending_space = false;

    for hit in hits {
        if hit.start > cursor {
            push_collapsed_segment(&mut core, &scan_body[cursor..hit.start], &mut pending_space);
        }
        if pending_space && !core.is_empty() {
            core.push(' ');
        }
        pending_space = false;
        let word = &scan_body[hit.start..hit.end];
        if mark_highlights {
            core.push('«');
            core.push_str(word);
            core.push('»');
        } else {
            core.push_str(word);
        }
        cursor = hit.end;
    }

    if cursor < win_end {
        push_collapsed_segment(&mut core, &scan_body[cursor..win_end], &mut pending_space);
    }

    if core.is_empty() {
        return String::new();
    }

    let mut out = String::with_capacity(core.len() + 6);
    if win_start > 0 {
        out.push('…');
    }
    out.push_str(&core);
    if has_more_after {
        out.push('…');
    }
    out
}

fn push_collapsed_segment(out: &mut String, segment: &str, pending_space: &mut bool) {
    for ch in segment.chars() {
        if ch.is_whitespace() {
            *pending_space = true;
        } else {
            if *pending_space && !out.is_empty() {
                out.push(' ');
            }
            *pending_space = false;
            out.push(ch);
        }
    }
}

#[inline]
fn floor_char_boundary(s: &str, mut idx: usize) -> usize {
    if idx >= s.len() {
        return s.len();
    }
    while idx > 0 && !s.is_char_boundary(idx) {
        idx -= 1;
    }
    idx
}

#[inline]
fn ceil_char_boundary(s: &str, mut idx: usize) -> usize {
    if idx >= s.len() {
        return s.len();
    }
    while idx < s.len() && !s.is_char_boundary(idx) {
        idx += 1;
    }
    idx
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_index() -> SymSpellIndex {
        SymSpellIndex::from_vocab([
            ("invoic".to_owned(), 120),
            ("receipt".to_owned(), 85),
            ("contract".to_owned(), 60),
            ("cafe".to_owned(), 25),
            ("reeel".to_owned(), 18),
            ("uber".to_owned(), 14),
            ("strasse".to_owned(), 9),
        ])
    }

    #[test]
    fn corrects_common_typos_and_transpositions() {
        let idx = sample_index();

        let m_invoce = idx.suggest("invoce", 0, 4);
        assert!(!m_invoce.is_empty());
        assert_eq!(m_invoce[0].term, "invoic");
        assert_eq!(m_invoce[0].distance, 1);

        let m_reciept = idx.suggest("reciept", 0, 4);
        assert!(!m_reciept.is_empty());
        assert_eq!(m_reciept[0].term, "receipt");
        assert_eq!(m_reciept[0].distance, 1);

        let m_niovce = idx.suggest("niovce", 0, 4);
        assert!(!m_niovce.is_empty());
        assert_eq!(m_niovce[0].term, "invoic");
        assert!(m_niovce[0].distance <= 2);

        let m_onvoice = idx.suggest("onvoice", 0, 4);
        assert!(!m_onvoice.is_empty());
        assert_eq!(m_onvoice[0].term, "invoic");
        assert_eq!(m_onvoice[0].distance, 1);
    }

    #[test]
    fn noisy_typo_override_expands_rare_exact_typo_to_frequent_term() {
        let idx = SymSpellIndex::from_vocab([
            ("invoc".to_owned(), 1),
            ("invoic".to_owned(), 50),
            ("invoca".to_owned(), 1),
        ]);

        // 1 log file has typo `invoce` (stemmed `invoc`, doc_freq = 1), while 50 files have `invoice` (`invoic`).
        let matches = idx.suggest("invoce", 1, 4);
        assert_eq!(matches.len(), 1);
        assert_eq!(matches[0].term, "invoic");
        assert_eq!(matches[0].distance, 1);
        assert_eq!(matches[0].doc_freq, 50);

        // Once exact_doc_freq >= 5, never fuzzy-expand.
        assert!(idx.suggest("invoce", 5, 4).is_empty());

        // When exact_doc_freq = 3, a candidate needs doc_freq >= 24; 10 is insufficient.
        let weak_idx = SymSpellIndex::from_vocab([("invoc".to_owned(), 3), ("invoic".to_owned(), 10)]);
        assert!(weak_idx.suggest("invoce", 3, 4).is_empty());
    }

    #[test]
    fn folds_european_diacritics_and_combining_marks() {
        assert_eq!(fold_diacritics("Café"), "cafe");
        assert_eq!(fold_diacritics("cafe\u{0301}"), "cafe");
        assert_eq!(fold_diacritics("reëel"), "reeel");
        assert_eq!(fold_diacritics("über"), "uber");
        assert_eq!(fold_diacritics("Straße"), "strasse");
        assert_eq!(fold_diacritics("façade niño"), "facade nino");

        let idx = sample_index();
        assert_eq!(idx.doc_freq("café"), 25);
        assert_eq!(idx.doc_freq("reëel"), 18);
        assert_eq!(idx.doc_freq("über"), 14);

        let c_typo = idx.suggest("caffé", 0, 3);
        assert!(!c_typo.is_empty());
        assert_eq!(c_typo[0].term, "cafe");
    }

    #[test]
    fn extracts_stem_and_diacritic_aware_snippets_with_optional_markers() {
        let filler = "Quarterly operations archive entry without relevant keywords. ".repeat(8);
        let target = "Approved Café invoicing summary: reëel Überweisung receipt #9042 paid in full.";
        let body = format!("{filler}\n\n   {target}   \n\n{filler}");
        let terms = vec![
            "cafe".to_owned(),
            "invoice".to_owned(),
            "reeel".to_owned(),
            "uber".to_owned(),
        ];

        let marked = extract_snippet(&body, &terms, 220, true);
        assert!(marked.text.contains("«Café»"));
        assert!(marked.text.contains("«invoicing»"));
        assert!(marked.text.contains("«reëel»"));
        assert!(marked.text.contains("«Überweisung»"));
        assert!(!marked.text.contains('\n'));
        assert!(marked.text.starts_with('…'));
        assert!(marked.text.ends_with('…'));
        assert_eq!(marked.matched_terms, vec!["cafe", "invoice", "reeel", "uber"]);

        let plain = extract_snippet(&body, &terms, 220, false);
        assert!(!plain.text.contains('«'));
        assert!(!plain.text.contains('»'));
        assert!(plain.text.contains("Approved Café invoicing summary: reëel Überweisung receipt #9042"));
        assert_eq!(plain.matched_terms.len(), 4);

        // Multi-byte UTF-8 boundary stress test across varying narrow/medium widths.
        let multibyte = "éëü€ßàö ".repeat(40) + "Café invoicing reëel " + &"éëü€ßàö ".repeat(40);
        for w in 40..90 {
            let s = extract_snippet(&multibyte, &terms, w, true);
            assert!(s.text.contains("«Café»") || s.text.contains("«invoicing»"));
        }
    }

    #[test]
    fn cached_vocab_loads_and_invalidates_on_stamp_change() {
        let db = Connection::open_in_memory().unwrap();
        db.execute_batch(
            "CREATE VIRTUAL TABLE content_fts USING fts5(
                body,
                tokenize='porter unicode61 remove_diacritics 2'
            );",
        )
        .unwrap();
        for _ in 0..12 {
            db.execute("INSERT INTO content_fts(body) VALUES ('Quarterly invoice receipt')", [])
                .unwrap();
        }

        let v1 = cached_vocab(&db, "mem-test-db", 12, 1000);
        let v2 = cached_vocab(&db, "mem-test-db", 12, 1000);
        assert!(Arc::ptr_eq(&v1, &v2));
        assert_eq!(v1.doc_freq("invoic"), 12);

        db.execute("INSERT INTO content_fts(body) VALUES ('Café contract')", [])
            .unwrap();
        let v3 = cached_vocab(&db, "mem-test-db", 13, 1001);
        assert!(!Arc::ptr_eq(&v1, &v3));
        assert_eq!(v3.doc_freq("cafe"), 1);
    }
}
