use std::{fs::File, io::{self, Read}, path::{Path,PathBuf}, sync::atomic::AtomicBool};
use flate2::read::ZlibDecoder;
use quick_xml::{Reader, events::Event};
use crate::{EngineResult,scanner::{RECALL_RISK,check_cancel,attributes,timestamp},util::invalid};

pub(super) const TEXT: &str = "txt md json xml csv log cs py js ts jsx tsx html htm css ini cfg yaml yml sql ps1 bat sh toml c cpp h java rs go";
pub(super) const OFFICE: &str = "docx xlsx pptx odt ods odp";

pub(crate) fn part_of(s:&str,list:&str)->bool { list.split_ascii_whitespace().any(|ext|ext==s) }

pub(crate) fn safe_source_path(volume:&str,mount:&str,rel:&str)->io::Result<PathBuf> {
    if !crate::content::safe_relative(rel) {return Err(invalid("Invalid catalog path"));}
    crate::scanner::verify_mount(volume,mount)?;
    #[cfg(windows)]
    let mut path=if mount.starts_with(r"\\?\") {
        PathBuf::from(mount)
    } else if let Some(mount)=mount.strip_prefix(r"\\") {
        PathBuf::from(format!(r"\\?\UNC\{mount}"))
    } else {
        PathBuf::from(format!(r"\\?\{mount}"))
    };
    #[cfg(not(windows))]
    let mut path=PathBuf::from(mount);
    for part in rel.split('\\') {
        path.push(part);
        if attributes(&std::fs::symlink_metadata(&path)?)&RECALL_RISK!=0 {
            return Err(invalid("Source path crosses a reparse point or recall-risk item"));
        }
    }
    Ok(path)
}
pub(crate) fn open_safe(path:&Path,bytes:i64,modified:i64)->io::Result<File> {
    let unchanged=|meta:&std::fs::Metadata| meta.is_file() && attributes(meta)&RECALL_RISK==0
        && i64::try_from(meta.len()).ok()==Some(bytes) && timestamp(meta.modified())==modified;
    if !unchanged(&std::fs::symlink_metadata(path)?) { return Err(invalid("Source metadata changed")); }
    let mut options=File::options();
    options.read(true);
    #[cfg(windows)] {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::{FILE_FLAG_OPEN_NO_RECALL,FILE_FLAG_OPEN_REPARSE_POINT};
        options.custom_flags(FILE_FLAG_OPEN_NO_RECALL|FILE_FLAG_OPEN_REPARSE_POINT).share_mode(1);
    }
    let file=options.open(path)?;
    if !unchanged(&file.metadata()?) { return Err(invalid("Source metadata changed")); }
    Ok(file)
}

pub(super) fn extract(file:File,extension:&str,cap:usize,cancel:&AtomicBool)->EngineResult<String> {
    let text=if part_of(extension,OFFICE) { office(file,extension,cap,cancel)? }
        else if extension=="pdf" { pdf(file,cap,cancel)? }
        else { plain(file,cap,cancel)? };
    Ok(clean(&text,cap))
}

fn read_chunks(mut input:impl Read,cap:usize,cancel:&AtomicBool)->io::Result<Vec<u8>> {
    let mut bytes=Vec::new();
    let mut chunk=[0u8;16384];
    while bytes.len()<cap {
        check_cancel(cancel)?;
        let length=chunk.len().min(cap-bytes.len());
        let count=input.read(&mut chunk[..length])?;
        if count==0 { break; }
        bytes.extend_from_slice(&chunk[..count]);
    }
    Ok(bytes)
}

fn plain(file:File,cap:usize,cancel:&AtomicBool)->io::Result<String> {
    let bytes=read_chunks(file,cap.saturating_mul(4).saturating_add(4096),cancel)?;
    Ok(decode_text(&bytes))
}

fn decode_text(bytes:&[u8])->String {
    if bytes.starts_with(&[255,254,0,0]) || bytes.starts_with(&[0,0,254,255]) {
        let little=bytes[0]==255;
        return bytes[4..].chunks_exact(4).map(|unit| {
            let unit=unit.try_into().unwrap();
            char::from_u32(if little {u32::from_le_bytes(unit)} else {u32::from_be_bytes(unit)}).unwrap_or('\u{fffd}')
        }).collect();
    }
    if bytes.starts_with(&[255,254]) || bytes.starts_with(&[254,255]) {
        let little=bytes[0]==255;
        let units:Vec<_>=bytes[2..].chunks_exact(2).map(|unit| {
            let unit=[unit[0],unit[1]];
            if little {u16::from_le_bytes(unit)} else {u16::from_be_bytes(unit)}
        }).collect();
        return String::from_utf16_lossy(&units);
    }
    if bytes.iter().take(4096).any(|&b|b==0) { return String::new(); }
    String::from_utf8_lossy(bytes.strip_prefix(&[239,187,191]).unwrap_or(bytes)).into_owned()
}

fn clean(text:&str,cap:usize)->String {
    let mut output=String::new();
    let mut space=false;
    let mut units=0;
    for c in text.chars() {
        if c.is_control() || c.is_whitespace() { space=!output.is_empty(); continue; }
        units+=c.len_utf16()+usize::from(space);
        if units>cap { break; }
        if space { output.push(' '); space=false; }
        output.push(c);
    }
    output
}

fn office(file:File,ext:&str,cap:usize,cancel:&AtomicBool)->EngineResult<String> {
    let mut archive=zip::ZipArchive::new(file)?;
    let mut names:Vec<_>=archive.file_names().map(str::to_owned).collect();
    names.sort_by_key(|name|(name!="xl/sharedStrings.xml",name.clone()));
    let mut text=String::new();
    let mut shared=Vec::new();
    let mut budget=cap.saturating_mul(32).clamp(4<<20,64<<20);
    for name in names {
        check_cancel(cancel)?;
        if text.len()>=cap*4 { break; }
        let include=match ext {
            "docx"=>name=="word/document.xml",
            "xlsx"=> name=="xl/sharedStrings.xml" || (name.starts_with("xl/worksheets/") && !name[14..].contains('/') && name.ends_with(".xml")),
            "pptx"=>name.starts_with("ppt/slides/slide") && name.ends_with(".xml") && !name[11..].contains('/'),
            _=>name=="content.xml",
        };
        if !include { continue; }
        let entry=archive.by_name(&name)?;
        if entry.size()>budget as u64 { continue; }
        let data=read_chunks(entry,budget+1,cancel)?;
        if data.len()>budget { return Err(invalid("Office XML exceeds extraction budget").into()); }
        budget-=data.len();
        let mut xml=Reader::from_reader(data.as_slice());
        let strings=name=="xl/sharedStrings.xml";
        let sheet=ext=="xlsx" && !strings;
        let odf=matches!(ext,"odt"|"ods"|"odp");
        let mut stack=Vec::<Vec<u8>>::new();
        let mut item=String::new();
        let mut shared_cell=false;
        loop {
            check_cancel(cancel)?;
            match xml.read_event()? {
                Event::Start(tag)=>{
                    let local=tag.local_name().as_ref().to_vec();
                    if sheet && local==b"c" {
                        shared_cell=tag.attributes().flatten().any(|a|a.key.as_ref()==b"t" && a.value.as_ref()==b"s");
                    }
                    if strings && local==b"si" { item.clear(); }
                    stack.push(local);
                }
                Event::End(tag)=>{
                    if strings && tag.local_name().as_ref()==b"si" { shared.push(std::mem::take(&mut item)); }
                    stack.pop();
                }
                Event::Text(value)=>{
                    let element=stack.last().map_or(&[][..],Vec::as_slice);
                    if odf || element==b"t" || (sheet && element==b"v") {
                        let value=value.unescape()?;
                        if strings { item.push_str(&value); }
                        else if sheet && element==b"v" && shared_cell {
                            if let Some(s)=value.trim().parse::<usize>().ok().and_then(|i|shared.get(i)) { append(&mut text,s); }
                        } else { append(&mut text,&value); }
                    }
                }
                Event::CData(value) if odf => append(&mut text,&String::from_utf8_lossy(&value)),
                Event::DocType(_) => return Err(invalid("Office XML contains a document type").into()),
                Event::Eof=>break,
                _=>{},
            }
            if text.len()>=cap*4 { break; }
        }
    }
    Ok(text)
}
fn append(text:&mut String,part:&str) { text.push_str(part); text.push(' '); }

// A bounded PDF content-stream reader for literal and hexadecimal Tj/TJ operands.
fn pdf(file:File,cap:usize,cancel:&AtomicBool)->EngineResult<String> {
    let all=read_chunks(file, (128<<20)+1, cancel)?;
    if !all.starts_with(b"%PDF-") || all.len()>128<<20 { return Ok(String::new()); }
    let find=|bytes:&[u8],pattern:&[u8]|bytes.windows(pattern.len()).position(|w|w==pattern);
    let mut cursor=0;
    let mut output=String::new();
    let mut budget=64usize<<20;
    while let Some(start)=find(&all[cursor..],b"stream") {
        check_cancel(cancel)?;
        let start=start+cursor;
        let eol=match all.get(start+6..start+8) {
            Some(b"\r\n")=>2, Some([b'\r'|b'\n',_])=>1, _=>{cursor=start+6;continue;},
        };
        let body=start+6+eol;
        let Some(end)=find(&all[body..],b"endstream").map(|n|n+body) else { break; };
        let dict_start=all[cursor..start].windows(2).rposition(|w|w==b"<<").map_or(cursor,|n|n+cursor);
        let dict=&all[dict_start..start];
        let raw=&all[body..end];
        let decoded;
        let stream=if find(dict,b"/FlateDecode").is_some() {
            decoded=match read_chunks(ZlibDecoder::new(raw),budget+1,cancel) {
                Ok(data)=>data, Err(e) if e.kind()==io::ErrorKind::Interrupted=>return Err(e.into()),
                Err(_)=>{ cursor=end+9;continue; }
            };
            decoded.as_slice()
        } else if find(dict,b"/Filter").is_some() { cursor=end+9;continue; }
        else { raw };
        if stream.len()>budget { break; }
        budget-=stream.len();
        pdf_stream(stream,&mut output,cap,cancel)?;
        if output.len()>=cap*4 { break; }
        cursor=end+9;
    }
    Ok(output)
}

fn pdf_stream(mut stream:&[u8],output:&mut String,cap:usize,cancel:&AtomicBool)->io::Result<()> {
    let mut pending=Vec::new();
    let mut text=false;
    let mut array=false;
    while let Some((&first,more))=stream.split_first() {
        check_cancel(cancel)?;
        stream=more;
        match first {
            b'%' => stream=&stream[stream.iter().position(|&b|b==b'\n'||b==b'\r').unwrap_or(stream.len())..],
            b'(' => {
                let (value,rest)=literal(stream);
                stream=rest;
                if text && pending.len()<cap*4 { pending.extend(value); }
            }
            b'<' if stream.first()!=Some(&b'<') => {
                let end=stream.iter().position(|&b|b==b'>').unwrap_or(stream.len());
                if text && pending.len()<cap*4 {
                    let mut digits=stream[..end].iter().filter(|b|!b.is_ascii_whitespace()).map(|&b|(b as char).to_digit(16));
                    while let Some(Some(high))=digits.next() {
                        let low=digits.next().flatten().unwrap_or(0);
                        pending.push((high*16+low) as u8);
                    }
                }
                stream=&stream[end.saturating_add(1).min(stream.len())..];
            }
            b'['=>{array=true;pending.clear();},
            b']'=>array=false,
            b'\''|b'"' if text=>{append(output,&decode_text(&pending));pending.clear();},
            b if b.is_ascii_alphabetic() => {
                let end=stream.iter().position(|b|!b.is_ascii_alphabetic() && *b!=b'*').unwrap_or(stream.len());
                let rest=&stream[..end];
                stream=&stream[end..];
                match (b,rest) {
                    (b'B',b"T")=>{text=true;pending.clear();},
                    (b'E',b"T")=>{text=false;pending.clear();},
                    (b'T',b"j"|b"J") if text=>{append(output,&decode_text(&pending));pending.clear();},
                    _ if !array=>pending.clear(),
                    _=>{},
                }
            }
            _=>{},
        }
        if output.len()>=cap*4 { break; }
    }
    Ok(())
}

fn literal(mut stream:&[u8])->(Vec<u8>,&[u8]) {
    let mut depth=1usize;
    let mut value=Vec::new();
    while let Some((&b,tail))=stream.split_first() {
        stream=tail;
        match b {
            b')'=>{depth-=1;if depth==0 {break;}value.push(b);},
            b'('=>{depth+=1;value.push(b);},
            b'\\'=>{
                let Some((&escaped,tail))=stream.split_first() else {break;};
                stream=tail;
                let byte=match escaped {
                    b'\r'|b'\n'=>{
                        if escaped==b'\r' && stream.first()==Some(&b'\n') {stream=&stream[1..];}
                        continue;
                    }
                    b'0'..=b'7'=>{
                        let mut octal=escaped-b'0';
                        for _ in 0..2 {
                            match stream.first() {
                                Some(b @ b'0'..=b'7')=>{octal=octal.wrapping_mul(8).wrapping_add(*b-b'0');stream=&stream[1..];},
                                _=>break,
                            }
                        }
                        octal
                    }
                    b'n'=>b'\n',b'r'=>b'\r',b't'=>b'\t',b'b'=>8,b'f'=>12,_=>escaped,
                };
                value.push(byte);
            }
            _=>value.push(b),
        }
    }
    (value,stream)
}


