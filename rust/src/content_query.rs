use std::{collections::HashMap, sync::OnceLock};
use crate::{content::ContentMatchMode, util::words};

#[derive(Clone, Debug, Eq, PartialEq, Hash)]
pub(crate) struct Term { pub text: String, pub prefix: bool, pub phrase: bool }

#[derive(Clone)]
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

    pub fn exclusions<'a>(&'a self, output: &mut Vec<&'a Self>) {
        match self {
            Self::Not(_) => output.push(self),
            Self::Join(a,b,false) => { a.exclusions(output); b.exclusions(output); }
            _=>{},
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
        if excluded || self.prefix || corrections.is_empty() { return exact; }
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
        return (!text.is_empty()).then_some((Node::Term(Term{text,prefix:false,phrase:true}),false));
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
                (!text.is_empty()).then_some(Node::Term(Term{text,prefix,phrase}))
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
