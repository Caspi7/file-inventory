use std::{io::{BufWriter,Write},path::Path,sync::atomic::AtomicBool};
use chrono::{Local,TimeZone};
use serde::{Serialize,Serializer};
use serde::ser::SerializeSeq;
use crate::{EngineResult,catalog::FileEntry,scanner::check_cancel,util::atomic_write};

pub const REPORT_TEMPLATE:&str=include_str!("../../ReportTemplate.html");
const COLUMNS:[&str;10]=["Name","Full path","Relative path","Volume ID","Drive label","Status","Bytes","Modified","Last seen","Kind"];

#[derive(Clone,Copy,Debug,Eq,PartialEq)]
pub enum InventoryExportFormat { Csv,Json,Html }

#[derive(Serialize)]
#[serde(rename_all="camelCase")]
struct ReportRow<'a> {
    name:&'a str,full_path:String,relative_path:&'a str,volume_id:&'a str,drive_label:&'a str,
    status:&'a str,bytes:i64,modified:String,last_seen:String,is_directory:bool,
}
impl<'a> From<&'a FileEntry> for ReportRow<'a> {
    fn from(row:&'a FileEntry)->Self {
        Self{name:&row.name,full_path:row.full_path().to_string_lossy().into_owned(),relative_path:&row.rel,
            volume_id:&row.volume,drive_label:&row.label,status:row.status(),bytes:row.bytes,
            modified:iso(row.modified),last_seen:iso(row.seen),is_directory:row.is_directory()}
    }
}
impl ReportRow<'_> {
    fn values(&self)->[String;10] {
        [self.name.into(),self.full_path.clone(),self.relative_path.into(),self.volume_id.into(),self.drive_label.into(),
         self.status.into(),self.bytes.to_string(),self.modified.clone(),self.last_seen.clone(),
         if self.is_directory {"Folder"}else{"File"}.into()]
    }
}

pub fn write(destination:&Path,rows:&[FileEntry],format:InventoryExportFormat,title:&str,cancel:&AtomicBool)->EngineResult<()> {
    check_cancel(cancel)?;
    atomic_write(destination,|file|->EngineResult<()> {
        let mut output=BufWriter::new(file);
        match format {
            InventoryExportFormat::Json=>{
                let mut json=serde_json::Serializer::pretty(&mut output);
                let mut sequence=json.serialize_seq(Some(rows.len()))?;
                for row in rows {check_cancel(cancel)?;sequence.serialize_element(&ReportRow::from(row))?;}
                sequence.end()?;
            }
            InventoryExportFormat::Csv=>{
                output.write_all(&[0xEF,0xBB,0xBF])?;
                write!(output,"{}\r\n",COLUMNS.map(csv).join(","))?;
                for row in rows {
                    check_cancel(cancel)?;
                    write!(output,"{}\r\n",ReportRow::from(row).values().map(|s|csv(&s)).join(","))?;
                }
            }
            InventoryExportFormat::Html=>{
                let (before,after)=REPORT_TEMPLATE.split_once("{{ROWS}}").expect("report template row marker");
                let title=html(title);
                output.write_all(before.replace("{{TITLE}}",&title).as_bytes())?;
                for row in rows {
                    check_cancel(cancel)?;
                    output.write_all(b"<tr>")?;
                    for value in ReportRow::from(row).values() {write!(output,"<td>{}</td>",html(&value))?;}
                    writeln!(output,"</tr>")?;
                }
                output.write_all(after.replace("{{TITLE}}",&title).as_bytes())?;
            }
        }
        output.flush()?;
        check_cancel(cancel)?;
        Ok(())
    })
}

fn iso(ms:i64)->String {
    Local.timestamp_millis_opt(ms).single().map(|at|at.format("%Y-%m-%dT%H:%M:%S%.3f0000%:z").to_string()).unwrap_or_default()
}
fn csv(value:&str)->String {
    let formula=value.trim_start().starts_with(['=','+','-','@']) || value.starts_with(['\t','\r','\n']);
    format!("\"{}{}\"",if formula{"'"}else{""},value.replace('"',"\"\""))
}
fn html(input:&str)->String {
    input.replace('&',"&amp;").replace('<',"&lt;").replace('>',"&gt;").replace('"',"&quot;").replace('\'',"&#39;")
}


