use std::{
    cmp::Ordering,
    collections::{BinaryHeap, HashMap, HashSet},
    fs::{self, File},
    io::{self, BufReader, Cursor, Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, Ordering as AtomicOrdering},
        Arc, Mutex,
    },
    time::Duration,
};

use rusqlite::{params, types::ValueRef, Connection, TransactionBehavior};
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::{
    catalog::{configure, Catalog},
    util::{interrupted, invalid, now_ms, read_json, write_json},
    EngineResult,
};

pub const VECTOR_SCHEMA: &str = "CREATE TABLE IF NOT EXISTS content_vectors(file_id INTEGER PRIMARY KEY, pack INTEGER NOT NULL, dims INTEGER NOT NULL, vec BLOB NOT NULL, updated_at INTEGER NOT NULL);";

pub const DEFAULT_MAX_TOKENS: usize = 512;
pub const DEFAULT_MIN_COSINE: f32 = 0.24;

const SETTINGS_FILE: &str = "semantic_settings.json";
const USER_AGENT: &str = concat!("FileInventory/", env!("CARGO_PKG_VERSION"));
const MAX_SAFETENSORS_HEADER: u64 = 16 * 1024 * 1024;
const MAX_SUBWORD_BYTES: usize = 64;
const BODY_PREFIX_CHARS: usize = 8_192;
const BODY_DECOMPRESS_LIMIT: u64 = (BODY_PREFIX_CHARS as u64) * 4;
const BACKFILL_BATCH: usize = 128;

#[derive(Copy, Clone, Debug, Default, Eq, PartialEq, Serialize, Deserialize)]
pub enum SemanticPack {
    #[default]
    Off,
    EnglishFast,
    Multilingual,
}

impl SemanticPack {
    pub const ALL: [Self; 3] = [Self::Off, Self::EnglishFast, Self::Multilingual];

    pub fn id(self) -> i64 {
        match self {
            Self::Off => 0,
            Self::EnglishFast => 1,
            Self::Multilingual => 2,
        }
    }

    pub fn from_id(id: i64) -> Self {
        match id {
            1 => Self::EnglishFast,
            2 => Self::Multilingual,
            _ => Self::Off,
        }
    }

    pub fn index(self) -> i32 {
        self.id() as i32
    }

    pub fn from_index(index: i32) -> Self {
        Self::from_id(i64::from(index))
    }

    pub fn is_enabled(self) -> bool {
        !matches!(self, Self::Off)
    }

    fn download_url(self) -> Option<&'static str> {
        match self {
            Self::Off => None,
            Self::EnglishFast => Some("https://github.com/Caspi7/file-inventory/releases/download/linguistic-packs-v1/linguistic-pack-en-v1.zip"),
            Self::Multilingual => Some("https://github.com/Caspi7/file-inventory/releases/download/linguistic-packs-v1/linguistic-pack-multilingual-v1.zip"),
        }
    }

    pub fn slug(self) -> Option<&'static str> {
        match self {
            Self::Off => None,
            Self::EnglishFast => Some("potion-base-8m"),
            Self::Multilingual => Some("minishlab/potion-multilingual-128m"),
        }
    }

    pub fn display_name(self) -> &'static str {
        match self {
            Self::Off => "Off",
            Self::EnglishFast => "English Linguistic Pack",
            Self::Multilingual => "Multilingual Linguistic Pack",
        }
    }

    pub fn dims(self) -> usize {
        match self {
            Self::Off => 0,
            Self::EnglishFast | Self::Multilingual => 256,
        }
    }
}

#[derive(Serialize, Deserialize)]
struct SemanticSettings {
    pack: SemanticPack,
}

pub fn load_pack_setting(data_dir: &Path) -> SemanticPack {
    let path = data_dir.join(SETTINGS_FILE);
    if let Ok(settings) = read_json::<SemanticSettings>(&path) {
        return settings.pack;
    }
    read_json::<SemanticPack>(&path).unwrap_or_default()
}

pub fn save_pack_setting(data_dir: &Path, pack: SemanticPack) -> io::Result<()> {
    fs::create_dir_all(data_dir)?;
    write_json(&data_dir.join(SETTINGS_FILE), &SemanticSettings { pack })
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PackInfo {
    pub pack: SemanticPack,
    pub installed: bool,
    pub disk_bytes: u64,
}

pub fn pack_dir(data_dir: &Path, pack: SemanticPack) -> Option<PathBuf> {
    pack.slug().map(|slug| data_dir.join("models").join(slug))
}

pub fn pack_info(data_dir: &Path, pack: SemanticPack) -> PackInfo {
    let Some(dir) = pack_dir(data_dir, pack) else {
        return PackInfo {
            pack,
            installed: false,
            disk_bytes: 0,
        };
    };
    let has_tokenizer = fs::metadata(dir.join("tokenizer.json"))
        .map(|m| m.is_file() && m.len() > 0)
        .unwrap_or(false);
    let has_model = fs::metadata(dir.join("model.safetensors"))
        .map(|m| m.is_file() && m.len() > 0)
        .unwrap_or(false);
    let mut disk_bytes = 0u64;
    if let Ok(entries) = fs::read_dir(&dir) {
        for entry in entries.flatten() {
            let name = entry.file_name();
            if name.to_string_lossy().ends_with(".part") {
                continue;
            }
            if let Ok(meta) = entry.metadata() {
                if meta.is_file() {
                    disk_bytes = disk_bytes.saturating_add(meta.len());
                }
            }
        }
    }
    PackInfo {
        pack,
        installed: has_tokenizer && has_model,
        disk_bytes,
    }
}

pub fn download_pack(
    data_dir: &Path,
    pack: SemanticPack,
    cancel: &AtomicBool,
    mut on_progress: impl FnMut(u64, u64, &str),
) -> EngineResult<()> {
    check_cancel(cancel)?;
    let (Some(url), Some(dir)) = (pack.download_url(), pack_dir(data_dir, pack)) else {
        return Err(invalid("Select a Linguistic Pack to download").into());
    };
    let parent = dir.parent().expect("pack directory has a parent");
    fs::create_dir_all(parent)?;
    let nonce = rand::random::<u128>();
    let staging = parent.join(format!(".pack-{nonce:032x}"));
    fs::create_dir(&staging)?;

    let result = (|| -> EngineResult<()> {
        let archive_name = url.rsplit('/').next().unwrap_or("linguistic-pack.zip");
        let part_path = staging.join(format!("{archive_name}.part"));
        let response = ureq::AgentBuilder::new()
            .timeout_connect(Duration::from_secs(5))
            .timeout_read(Duration::from_secs(15))
            .timeout(Duration::from_secs(300))
            .build()
            .get(url)
            .set("User-Agent", USER_AGENT)
            .call()
            .map_err(|err| io::Error::other(format!("Failed to download {archive_name}: {err}")))?;
        let total = response
            .header("Content-Length")
            .and_then(|value| value.parse::<u64>().ok())
            .unwrap_or(0);
        let mut reader = response.into_reader();
        let mut output = File::create(&part_path)?;
        on_progress(0, total, archive_name);
        copy_pack_bytes(&mut reader, &mut output, cancel, |done| {
            on_progress(done, if total == 0 { 0 } else { total.max(done) }, archive_name);
        })?;
        drop(output);
        drop(reader);

        let extracted = staging.join("files");
        fs::create_dir(&extracted)?;
        extract_pack(File::open(&part_path)?, &extracted, cancel)?;
        fs::remove_file(&part_path)?;
        let embedder = Arc::new(StaticEmbedder::from_dir(&extracted)?);

        let mut cache = EMBEDDER_CACHE.lock().unwrap_or_else(|err| err.into_inner());
        check_cancel(cancel)?;
        let backup = parent.join(format!(".pack-backup-{nonce:032x}"));
        let backed_up = match fs::rename(&dir, &backup) {
            Ok(()) => true,
            Err(err) if err.kind() == io::ErrorKind::NotFound => false,
            Err(err) => return Err(err.into()),
        };
        if let Err(err) = fs::rename(&extracted, &dir) {
            if backed_up {
                fs::rename(&backup, &dir).map_err(|restore_err| {
                    io::Error::other(format!("Could not install pack: {err}. Could not restore previous pack from {}: {restore_err}", backup.display()))
                })?;
            }
            return Err(err.into());
        }
        *cache = Some((data_dir.to_path_buf(), pack, embedder));
        drop(cache);
        if backed_up {
            let _ = fs::remove_dir_all(backup);
        }
        Ok(())
    })();

    let _ = fs::remove_dir_all(staging);
    result
}

fn extract_pack(archive: impl Read + Seek, dir: &Path, cancel: &AtomicBool) -> EngineResult<()> {
    const FILES: [&str; 3] = ["config.json", "tokenizer.json", "model.safetensors"];
    let mut archive = zip::ZipArchive::new(archive)?;
    if archive.len() != FILES.len() {
        return Err(invalid("Linguistic Pack archive must contain exactly three files").into());
    }
    let mut seen = HashSet::new();
    for index in 0..archive.len() {
        check_cancel(cancel)?;
        let mut entry = archive.by_index(index)?;
        let name = entry.name().to_owned();
        // Exact allowlist requires a single root-level filename, without normalization.
        if !FILES.contains(&name.as_str())
            || !entry.is_file()
            || entry.is_symlink()
            || entry.size() == 0
            || !seen.insert(name.clone())
        {
            return Err(invalid(format!("Invalid Linguistic Pack archive entry: {name}")).into());
        }
        let mut output = File::create(dir.join(name))?;
        copy_pack_bytes(&mut entry, &mut output, cancel, |_| {})?;
    }
    Ok(())
}

fn copy_pack_bytes(
    reader: &mut impl Read,
    output: &mut File,
    cancel: &AtomicBool,
    mut on_progress: impl FnMut(u64),
) -> io::Result<()> {
    let mut copied = 0;
    let mut buffer = [0u8; 65_536];
    loop {
        check_cancel(cancel)?;
        let count = reader.read(&mut buffer)?;
        if count == 0 {
            return Ok(());
        }
        output.write_all(&buffer[..count])?;
        copied += count as u64;
        on_progress(copied);
    }
}


pub fn remove_pack(data_dir: &Path, pack: SemanticPack) -> EngineResult<()> {
    evict_cached_pack(data_dir, pack);
    if let Some(dir) = pack_dir(data_dir, pack) {
        match fs::remove_dir_all(&dir) {
            Ok(()) => {}
            Err(err) if err.kind() == io::ErrorKind::NotFound => {}
            Err(err) => return Err(err.into()),
        }
    }
    Ok(())
}

/// Pure-Rust IEEE-754 half-precision (`binary16`) to single-precision (`binary32`) conversion.
#[inline]
pub fn f16_to_f32(bits: u16) -> f32 {
    let sign = ((bits & 0x8000) as u32) << 16;
    let exp = ((bits >> 10) & 0x001f) as u32;
    let mant = (bits & 0x03ff) as u32;
    match exp {
        0 => {
            if mant == 0 {
                f32::from_bits(sign)
            } else {
                let mut m = mant;
                let mut e = 113u32;
                while (m & 0x0400) == 0 {
                    m <<= 1;
                    e -= 1;
                }
                m &= 0x03ff;
                f32::from_bits(sign | (e << 23) | (m << 13))
            }
        }
        31 => f32::from_bits(sign | 0x7f80_0000 | (mant << 13)),
        _ => f32::from_bits(sign | ((exp + 112) << 23) | (mant << 13)),
    }
}

/// Pure-Rust `bfloat16` to single-precision (`binary32`) conversion.
#[inline]
pub fn bf16_to_f32(bits: u16) -> f32 {
    f32::from_bits((bits as u32) << 16)
}

#[derive(Copy, Clone, Debug, Eq, PartialEq)]
enum TensorDtype {
    F32,
    F16,
    BF16,
}

impl TensorDtype {
    fn elem_bytes(self) -> usize {
        match self {
            Self::F32 => 4,
            Self::F16 | Self::BF16 => 2,
        }
    }
}

#[derive(Deserialize)]
struct TensorHeaderEntry {
    dtype: String,
    shape: Vec<usize>,
    data_offsets: [usize; 2],
}

struct QuantizedMatrix {
    vocab_size: usize,
    dims: usize,
    weights: Vec<i8>,
    scales: Vec<f32>,
}

fn parse_safetensors_reader<R: Read + Seek>(reader: &mut R) -> io::Result<QuantizedMatrix> {
    let total_len = reader.seek(SeekFrom::End(0))?;
    if total_len < 8 {
        return Err(invalid("SafeTensors file is too small"));
    }
    reader.seek(SeekFrom::Start(0))?;

    let mut len_buf = [0u8; 8];
    reader.read_exact(&mut len_buf)?;
    let header_size = u64::from_le_bytes(len_buf);
    if header_size == 0 || header_size > MAX_SAFETENSORS_HEADER {
        return Err(invalid("Invalid SafeTensors header size"));
    }
    let data_start = 8u64
        .checked_add(header_size)
        .filter(|&end| end <= total_len)
        .ok_or_else(|| invalid("Truncated SafeTensors header"))?;

    let mut header_bytes = vec![0u8; header_size as usize];
    reader.read_exact(&mut header_bytes)?;
    let header_map: serde_json::Map<String, Value> = serde_json::from_slice(&header_bytes)
        .map_err(|e| invalid(format!("Malformed SafeTensors JSON header: {e}")))?;

    let mut chosen: Option<(String, TensorHeaderEntry)> = None;
    if let Some(val) = header_map.get("embeddings") {
        if let Ok(entry) = serde_json::from_value::<TensorHeaderEntry>(val.clone()) {
            if entry.shape.len() == 2 {
                chosen = Some(("embeddings".to_owned(), entry));
            }
        }
    }
    if chosen.is_none() {
        let mut keys: Vec<&String> = header_map
            .keys()
            .filter(|k| k.as_str() != "__metadata__")
            .collect();
        keys.sort();
        for key in keys {
            if let Ok(entry) = serde_json::from_value::<TensorHeaderEntry>(header_map[key].clone()) {
                if entry.shape.len() == 2 && entry.shape[0] > 0 && entry.shape[1] > 0 {
                    chosen = Some((key.clone(), entry));
                    break;
                }
            }
        }
    }

    let (_name, entry) = chosen.ok_or_else(|| invalid("No 2D embedding tensor found in SafeTensors"))?;
    let dtype = match entry.dtype.as_str() {
        "F32" => TensorDtype::F32,
        "F16" => TensorDtype::F16,
        "BF16" => TensorDtype::BF16,
        other => return Err(invalid(format!("Unsupported SafeTensors dtype: {other}"))),
    };

    let vocab_size = entry.shape[0];
    let dims = entry.shape[1];
    if vocab_size == 0 || dims == 0 || dims > 8_192 {
        return Err(invalid("Invalid SafeTensors embedding dimensions"));
    }

    let [start_offset, end_offset] = entry.data_offsets;
    if start_offset > end_offset {
        return Err(invalid("Invalid SafeTensors data_offsets"));
    }
    let expected_bytes = vocab_size
        .checked_mul(dims)
        .and_then(|elems| elems.checked_mul(dtype.elem_bytes()))
        .ok_or_else(|| invalid("SafeTensors tensor size overflow"))?;
    if end_offset - start_offset != expected_bytes {
        return Err(invalid("SafeTensors byte length does not match tensor shape"));
    }
    let tensor_end = data_start
        .checked_add(end_offset as u64)
        .ok_or_else(|| invalid("SafeTensors offset overflow"))?;
    if tensor_end > total_len {
        return Err(invalid("SafeTensors tensor extends past end of file"));
    }

    reader.seek(SeekFrom::Start(data_start + start_offset as u64))?;
    let mut buffered = BufReader::with_capacity(65_536, reader);

    let total_elems = vocab_size * dims;
    let mut weights = vec![0i8; total_elems];
    let mut scales = vec![0.0f32; vocab_size];
    let mut raw_row = vec![0u8; dims * dtype.elem_bytes()];
    let mut float_row = vec![0.0f32; dims];

    for row_idx in 0..vocab_size {
        buffered.read_exact(&mut raw_row)?;
        let mut max_abs = 0.0f32;
        match dtype {
            TensorDtype::F32 => {
                for (d, chunk) in raw_row.chunks_exact(4).enumerate() {
                    let v = f32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]);
                    let clean = if v.is_finite() { v } else { 0.0 };
                    float_row[d] = clean;
                    max_abs = max_abs.max(clean.abs());
                }
            }
            TensorDtype::F16 => {
                for (d, chunk) in raw_row.chunks_exact(2).enumerate() {
                    let v = f16_to_f32(u16::from_le_bytes([chunk[0], chunk[1]]));
                    let clean = if v.is_finite() { v } else { 0.0 };
                    float_row[d] = clean;
                    max_abs = max_abs.max(clean.abs());
                }
            }
            TensorDtype::BF16 => {
                for (d, chunk) in raw_row.chunks_exact(2).enumerate() {
                    let v = bf16_to_f32(u16::from_le_bytes([chunk[0], chunk[1]]));
                    let clean = if v.is_finite() { v } else { 0.0 };
                    float_row[d] = clean;
                    max_abs = max_abs.max(clean.abs());
                }
            }
        }
        if max_abs > 1e-12 {
            scales[row_idx] = max_abs / 127.0;
            let inv_scale = 127.0 / max_abs;
            let dst = &mut weights[row_idx * dims..(row_idx + 1) * dims];
            for (out, &v) in dst.iter_mut().zip(float_row.iter()) {
                *out = (v * inv_scale).round().clamp(-127.0, 127.0) as i8;
            }
        }
    }

    Ok(QuantizedMatrix {
        vocab_size,
        dims,
        weights,
        scales,
    })
}

struct FastTokenizer {
    vocab: HashMap<String, usize>,
    ignored_ids: HashSet<usize>,
    lowercase: bool,
    strip_accents: bool,
    continuing_prefix: String,
    has_sp_space: bool,
    has_gpt_space: bool,
}

impl FastTokenizer {
    fn from_json_slice(bytes: &[u8]) -> io::Result<Self> {
        let root: Value = serde_json::from_slice(bytes)
            .map_err(|e| invalid(format!("Malformed tokenizer.json: {e}")))?;

        let mut lowercase = false;
        let mut strip_accents = false;
        if let Some(normalizer) = root.get("normalizer") {
            inspect_normalizer(normalizer, &mut lowercase, &mut strip_accents);
        }

        let model = root
            .get("model")
            .ok_or_else(|| invalid("tokenizer.json is missing 'model' section"))?;
        let model_type = model
            .get("type")
            .and_then(Value::as_str)
            .unwrap_or_default();

        let vocab_val = model
            .get("vocab")
            .ok_or_else(|| invalid("tokenizer.json is missing 'model.vocab'"))?;

        let mut vocab = HashMap::new();
        match vocab_val {
            Value::Object(map) => {
                vocab.reserve(map.len());
                for (token, id_val) in map {
                    if let Some(id) = id_val.as_u64() {
                        vocab.insert(token.clone(), id as usize);
                    }
                }
            }
            Value::Array(items) => {
                vocab.reserve(items.len());
                for (idx, item) in items.iter().enumerate() {
                    if let Some(piece) = item
                        .as_array()
                        .and_then(|arr| arr.first())
                        .and_then(Value::as_str)
                    {
                        vocab.entry(piece.to_owned()).or_insert(idx);
                    } else if let Some(piece) = item.as_str() {
                        vocab.entry(piece.to_owned()).or_insert(idx);
                    }
                }
            }
            _ => return Err(invalid("Unsupported 'model.vocab' structure in tokenizer.json")),
        }

        if vocab.is_empty() {
            return Err(invalid("Empty vocabulary in tokenizer.json"));
        }

        let mut ignored_ids = HashSet::new();
        if let Some(unk_id) = model.get("unk_id").and_then(Value::as_u64) {
            ignored_ids.insert(unk_id as usize);
        }
        if let Some(unk_tok) = model.get("unk_token").and_then(Value::as_str) {
            if let Some(&id) = vocab.get(unk_tok) {
                ignored_ids.insert(id);
            }
        }
        for special in [
            "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "<pad>", "<unk>", "<s>", "</s>", "<mask>",
        ] {
            if let Some(&id) = vocab.get(special) {
                ignored_ids.insert(id);
            }
        }

        if let Some(added) = root.get("added_tokens").and_then(Value::as_array) {
            for item in added {
                let Some(id) = item.get("id").and_then(Value::as_u64).map(|v| v as usize) else {
                    continue;
                };
                let is_special = item
                    .get("special")
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                if is_special {
                    ignored_ids.insert(id);
                } else if let Some(content) = item.get("content").and_then(Value::as_str) {
                    vocab.entry(content.to_owned()).or_insert(id);
                }
            }
        }

        let mut has_sp_space = false;
        let mut has_gpt_space = false;
        let mut has_wordpiece_hash = false;
        for key in vocab.keys() {
            if key.starts_with('\u{2581}') {
                has_sp_space = true;
            } else if key.starts_with('\u{0120}') {
                has_gpt_space = true;
            } else if key.starts_with("##") && key.len() > 2 {
                has_wordpiece_hash = true;
            }
        }

        let continuing_prefix = model
            .get("continuing_subword_prefix")
            .and_then(Value::as_str)
            .map(str::to_owned)
            .unwrap_or_else(|| {
                if model_type.eq_ignore_ascii_case("WordPiece") || has_wordpiece_hash {
                    "##".to_owned()
                } else {
                    String::new()
                }
            });

        if root.get("normalizer").is_none() || root.get("normalizer") == Some(&Value::Null) {
            // If no normalizer was serialized and the vocabulary contains no uppercase ASCII words,
            // treat the vocabulary as uncased so mixed-case queries still hit exact tokens.
            let has_uppercase_token = vocab.keys().any(|tok| {
                !ignored_ids.contains(&vocab[tok])
                    && tok
                        .trim_start_matches(['\u{2581}', '\u{0120}', '#'])
                        .chars()
                        .any(|c| c.is_ascii_uppercase())
            });
            if !has_uppercase_token {
                lowercase = true;
            }
        }

        Ok(Self {
            vocab,
            ignored_ids,
            lowercase,
            strip_accents,
            continuing_prefix,
            has_sp_space,
            has_gpt_space,
        })
    }

    fn normalize(&self, text: &str) -> String {
        let mut out = String::with_capacity(text.len());
        for ch in text.chars() {
            if ch == '\0' {
                continue;
            }
            if self.strip_accents && is_combining_mark(ch) {
                continue;
            }
            let base = if self.strip_accents {
                fold_latin_accent(ch)
            } else {
                ch
            };
            if self.lowercase {
                for lc in base.to_lowercase() {
                    if !(self.strip_accents && is_combining_mark(lc)) {
                        out.push(lc);
                    }
                }
            } else {
                out.push(base);
            }
        }
        out
    }

    fn lookup_valid(&self, token: &str) -> Option<usize> {
        self.vocab
            .get(token)
            .copied()
            .filter(|id| !self.ignored_ids.contains(id))
    }

    fn lookup_whole_word(&self, word: &str, scratch: &mut String) -> Option<usize> {
        if self.has_sp_space {
            scratch.clear();
            scratch.push('\u{2581}');
            scratch.push_str(word);
            if let Some(id) = self.lookup_valid(scratch) {
                return Some(id);
            }
            if let Some(id) = self.lookup_valid(word) {
                return Some(id);
            }
        } else if self.has_gpt_space {
            scratch.clear();
            scratch.push('\u{0120}');
            scratch.push_str(word);
            if let Some(id) = self.lookup_valid(scratch) {
                return Some(id);
            }
            if let Some(id) = self.lookup_valid(word) {
                return Some(id);
            }
        } else {
            if let Some(id) = self.lookup_valid(word) {
                return Some(id);
            }
            scratch.clear();
            scratch.push('\u{2581}');
            scratch.push_str(word);
            if let Some(id) = self.lookup_valid(scratch) {
                return Some(id);
            }
            scratch.clear();
            scratch.push('\u{0120}');
            scratch.push_str(word);
            if let Some(id) = self.lookup_valid(scratch) {
                return Some(id);
            }
        }
        None
    }

    fn tokenize_word(&self, word: &str, max_tokens: usize, scratch: &mut String, out: &mut Vec<usize>) {
        if word.is_empty() || out.len() >= max_tokens {
            return;
        }
        if let Some(id) = self.lookup_whole_word(word, scratch) {
            out.push(id);
            return;
        }
        if !self.lowercase && word.chars().any(char::is_uppercase) {
            let lowered_fallback = word.to_lowercase();
            if let Some(id) = self.lookup_whole_word(&lowered_fallback, scratch) {
                out.push(id);
                return;
            }
            let before = out.len();
            self.segment_subwords(word, max_tokens, scratch, out);
            if out.len() == before {
                self.segment_subwords(&lowered_fallback, max_tokens, scratch, out);
            }
            return;
        }

        self.segment_subwords(word, max_tokens, scratch, out);
    }

    fn segment_subwords(
        &self,
        word: &str,
        max_tokens: usize,
        scratch: &mut String,
        out: &mut Vec<usize>,
    ) {
        let mut pos = 0usize;
        while pos < word.len() && out.len() < max_tokens {
            let tail = &word[pos..];
            let cap_len = tail
                .char_indices()
                .map(|(i, c)| i + c.len_utf8())
                .take_while(|&end| end <= MAX_SUBWORD_BYTES)
                .last()
                .unwrap_or_else(|| tail.chars().next().map_or(tail.len(), char::len_utf8));
            let window = &tail[..cap_len];

            let mut matched: Option<(Option<usize>, usize)> = None;
            let mut end = window.len();
            while end > 0 {
                let piece = &window[..end];
                let found = if pos == 0 {
                    self.lookup_initial_piece(piece, scratch)
                } else {
                    self.lookup_continuing_piece(piece, scratch)
                };
                if let Some(id) = found {
                    matched = Some((Some(id), pos + end));
                    break;
                }
                end -= 1;
                while end > 0 && !window.is_char_boundary(end) {
                    end -= 1;
                }
            }

            if let Some((maybe_id, next_pos)) = matched {
                if let Some(id) = maybe_id {
                    out.push(id);
                }
                pos = next_pos;
            } else {
                let step = tail.chars().next().map_or(1, char::len_utf8);
                pos += step;
            }
        }
    }

    fn lookup_initial_piece(&self, piece: &str, scratch: &mut String) -> Option<usize> {
        if self.has_sp_space {
            scratch.clear();
            scratch.push('\u{2581}');
            scratch.push_str(piece);
            if let Some(&id) = self.vocab.get(scratch.as_str()) {
                return (!self.ignored_ids.contains(&id)).then_some(id);
            }
        } else if self.has_gpt_space {
            scratch.clear();
            scratch.push('\u{0120}');
            scratch.push_str(piece);
            if let Some(&id) = self.vocab.get(scratch.as_str()) {
                return (!self.ignored_ids.contains(&id)).then_some(id);
            }
        }
        self.lookup_valid(piece)
    }

    fn lookup_continuing_piece(&self, piece: &str, scratch: &mut String) -> Option<usize> {
        if !self.continuing_prefix.is_empty() {
            scratch.clear();
            scratch.push_str(&self.continuing_prefix);
            scratch.push_str(piece);
            if let Some(&id) = self.vocab.get(scratch.as_str()) {
                return (!self.ignored_ids.contains(&id)).then_some(id);
            }
        }
        if let Some(id) = self.lookup_valid(piece) {
            return Some(id);
        }
        if self.continuing_prefix.is_empty() {
            scratch.clear();
            scratch.push_str("##");
            scratch.push_str(piece);
            if let Some(&id) = self.vocab.get(scratch.as_str()) {
                return (!self.ignored_ids.contains(&id)).then_some(id);
            }
        }
        None
    }

    fn tokenize(&self, text: &str, max_tokens: usize) -> Vec<usize> {
        if max_tokens == 0 || text.is_empty() {
            return Vec::new();
        }
        let normalized = self.normalize(text);
        let mut out = Vec::with_capacity(max_tokens.min(64));
        let mut scratch = String::with_capacity(MAX_SUBWORD_BYTES + 8);

        let mut word_start: Option<usize> = None;
        for (idx, ch) in normalized.char_indices() {
            if is_word_char(ch) {
                if word_start.is_none() {
                    word_start = Some(idx);
                }
            } else if let Some(start) = word_start.take() {
                self.tokenize_word(&normalized[start..idx], max_tokens, &mut scratch, &mut out);
                if out.len() >= max_tokens {
                    return out;
                }
            }
        }
        if let Some(start) = word_start {
            self.tokenize_word(&normalized[start..], max_tokens, &mut scratch, &mut out);
        }
        out
    }
}

fn inspect_normalizer(val: &Value, lowercase: &mut bool, strip_accents: &mut bool) {
    let Some(obj) = val.as_object() else {
        return;
    };
    let kind = obj.get("type").and_then(Value::as_str).unwrap_or_default();
    match kind {
        "Lowercase" => *lowercase = true,
        "StripAccents" => *strip_accents = true,
        "BertNormalizer" => {
            let lc = obj.get("lowercase").and_then(Value::as_bool).unwrap_or(true);
            if lc {
                *lowercase = true;
            }
            let sa = obj
                .get("strip_accents")
                .and_then(Value::as_bool)
                .unwrap_or(lc);
            if sa {
                *strip_accents = true;
            }
        }
        "Sequence" => {
            if let Some(items) = obj.get("normalizers").and_then(Value::as_array) {
                for child in items {
                    inspect_normalizer(child, lowercase, strip_accents);
                }
            }
        }
        _ => {
            if obj.get("lowercase").and_then(Value::as_bool) == Some(true) {
                *lowercase = true;
            }
            if obj.get("strip_accents").and_then(Value::as_bool) == Some(true) {
                *strip_accents = true;
            }
        }
    }
}

#[inline]
fn is_combining_mark(c: char) -> bool {
    matches!(
        c,
        '\u{0300}'..='\u{036F}'
            | '\u{1AB0}'..='\u{1AFF}'
            | '\u{1DC0}'..='\u{1DFF}'
            | '\u{20D0}'..='\u{20FF}'
            | '\u{FE20}'..='\u{FE2F}'
    )
}

#[inline]
fn is_word_char(c: char) -> bool {
    c.is_alphanumeric() || is_combining_mark(c)
}

#[inline]
fn fold_latin_accent(c: char) -> char {
    match c {
        'À' | 'Á' | 'Â' | 'Ã' | 'Ä' | 'Å' | 'Ā' | 'Ă' | 'Ą' => 'A',
        'à' | 'á' | 'â' | 'ã' | 'ä' | 'å' | 'ā' | 'ă' | 'ą' => 'a',
        'Ç' | 'Ć' | 'Č' | 'Ĉ' | 'Ċ' => 'C',
        'ç' | 'ć' | 'č' | 'ĉ' | 'ċ' => 'c',
        'Ď' | 'Đ' => 'D',
        'ď' | 'đ' => 'd',
        'È' | 'É' | 'Ê' | 'Ë' | 'Ē' | 'Ĕ' | 'Ė' | 'Ę' | 'Ě' => 'E',
        'è' | 'é' | 'ê' | 'ë' | 'ē' | 'ĕ' | 'ė' | 'ę' | 'ě' => 'e',
        'Ĝ' | 'Ğ' | 'Ġ' | 'Ģ' => 'G',
        'ĝ' | 'ğ' | 'ġ' | 'ģ' => 'g',
        'Ì' | 'Í' | 'Î' | 'Ï' | 'Ĩ' | 'Ī' | 'Ĭ' | 'Į' | 'İ' => 'I',
        'ì' | 'í' | 'î' | 'ï' | 'ĩ' | 'ī' | 'ĭ' | 'į' | 'ı' => 'i',
        'Ł' | 'Ĺ' | 'Ļ' | 'Ľ' => 'L',
        'ł' | 'ĺ' | 'ļ' | 'ľ' => 'l',
        'Ñ' | 'Ń' | 'Ņ' | 'Ň' => 'N',
        'ñ' | 'ń' | 'ņ' | 'ň' => 'n',
        'Ò' | 'Ó' | 'Ô' | 'Õ' | 'Ö' | 'Ø' | 'Ō' | 'Ŏ' | 'Ő' => 'O',
        'ò' | 'ó' | 'ô' | 'õ' | 'ö' | 'ø' | 'ō' | 'ŏ' | 'ő' => 'o',
        'Ŕ' | 'Ŗ' | 'Ř' => 'R',
        'ŕ' | 'ŗ' | 'ř' => 'r',
        'Ś' | 'Ŝ' | 'Ş' | 'Š' => 'S',
        'ś' | 'ŝ' | 'ş' | 'š' => 's',
        'Ţ' | 'Ť' | 'Ŧ' => 'T',
        'ţ' | 'ť' | 'ŧ' => 't',
        'Ù' | 'Ú' | 'Û' | 'Ü' | 'Ũ' | 'Ū' | 'Ŭ' | 'Ů' | 'Ű' | 'Ų' => 'U',
        'ù' | 'ú' | 'û' | 'ü' | 'ũ' | 'ū' | 'ŭ' | 'ů' | 'ű' | 'ų' => 'u',
        'Ý' | 'Ÿ' | 'Ŷ' => 'Y',
        'ý' | 'ÿ' | 'ŷ' => 'y',
        'Ź' | 'Ż' | 'Ž' => 'Z',
        'ź' | 'ż' | 'ž' => 'z',
        _ => c,
    }
}

pub struct StaticEmbedder {
    vocab_size: usize,
    dims: usize,
    weights: Vec<i8>,
    scales: Vec<f32>,
    tokenizer: FastTokenizer,
}

impl StaticEmbedder {
    pub fn from_dir(model_dir: &Path) -> EngineResult<Self> {
        let tokenizer_bytes = fs::read(model_dir.join("tokenizer.json"))?;
        let tokenizer = FastTokenizer::from_json_slice(&tokenizer_bytes)?;
        let mut safetensors_file = File::open(model_dir.join("model.safetensors"))?;
        let matrix = parse_safetensors_reader(&mut safetensors_file)?;
        Ok(Self {
            vocab_size: matrix.vocab_size,
            dims: matrix.dims,
            weights: matrix.weights,
            scales: matrix.scales,
            tokenizer,
        })
    }

    pub fn from_bytes(safetensors_bytes: &[u8], tokenizer_json: &[u8]) -> EngineResult<Self> {
        let tokenizer = FastTokenizer::from_json_slice(tokenizer_json)?;
        let mut cursor = Cursor::new(safetensors_bytes);
        let matrix = parse_safetensors_reader(&mut cursor)?;
        Ok(Self {
            vocab_size: matrix.vocab_size,
            dims: matrix.dims,
            weights: matrix.weights,
            scales: matrix.scales,
            tokenizer,
        })
    }

    pub fn dims(&self) -> usize {
        self.dims
    }

    pub fn vocab_size(&self) -> usize {
        self.vocab_size
    }

    pub fn tokenize(&self, text: &str, max_tokens: usize) -> Vec<usize> {
        self.tokenizer.tokenize(text, max_tokens)
    }

    pub fn encode_text(&self, text: &str, max_tokens: usize) -> Option<Vec<i8>> {
        if self.dims == 0 || max_tokens == 0 {
            return None;
        }
        let tokens = self.tokenizer.tokenize(text, max_tokens);
        if tokens.is_empty() {
            return None;
        }

        let mut pooled = vec![0.0f32; self.dims];
        let mut valid_count = 0usize;

        for token_id in tokens {
            if token_id >= self.vocab_size {
                continue;
            }
            let scale = self.scales[token_id];
            if scale <= 0.0 {
                continue;
            }
            let row = &self.weights[token_id * self.dims..(token_id + 1) * self.dims];
            for (acc, &w) in pooled.iter_mut().zip(row.iter()) {
                *acc += (w as f32) * scale;
            }
            valid_count += 1;
        }

        if valid_count == 0 {
            return None;
        }

        let inv_count = 1.0 / (valid_count as f32);
        let mut norm_sq = 0.0f32;
        for val in &mut pooled {
            *val *= inv_count;
            norm_sq += *val * *val;
        }

        let norm = norm_sq.sqrt();
        if !norm.is_finite() || norm < 1e-6 {
            return None;
        }

        let scale = 127.0 / norm;
        let quantized: Vec<i8> = pooled
            .into_iter()
            .map(|v| (v * scale).round().clamp(-127.0, 127.0) as i8)
            .collect();

        quantized.iter().any(|&x| x != 0).then_some(quantized)
    }
}

static EMBEDDER_CACHE: Mutex<Option<(PathBuf, SemanticPack, Arc<StaticEmbedder>)>> = Mutex::new(None);

pub fn load_embedder(data_dir: &Path, pack: SemanticPack) -> EngineResult<Arc<StaticEmbedder>> {
    if !pack.is_enabled() {
        return Err(invalid("Linguistic Search is set to Off").into());
    }
    if let Ok(guard) = EMBEDDER_CACHE.lock() {
        if let Some((cached_dir, cached_pack, embedder)) = guard.as_ref() {
            if *cached_pack == pack && cached_dir == data_dir {
                return Ok(Arc::clone(embedder));
            }
        }
    }

    let dir = pack_dir(data_dir, pack).ok_or_else(|| invalid("Linguistic Pack is disabled"))?;
    let loaded = Arc::new(StaticEmbedder::from_dir(&dir)?);
    if let Ok(mut guard) = EMBEDDER_CACHE.lock() {
        *guard = Some((data_dir.to_path_buf(), pack, Arc::clone(&loaded)));
    }
    Ok(loaded)
}

pub fn clear_embedder_cache() {
    if let Ok(mut guard) = EMBEDDER_CACHE.lock() {
        *guard = None;
    }
}

fn evict_cached_pack(data_dir: &Path, pack: SemanticPack) {
    if let Ok(mut guard) = EMBEDDER_CACHE.lock() {
        if guard
            .as_ref()
            .is_some_and(|(dir, cached_pack, _)| *cached_pack == pack && dir == data_dir)
        {
            *guard = None;
        }
    }
}

pub fn encode_query(
    data_dir: &Path,
    pack: SemanticPack,
    query: &str,
) -> EngineResult<Option<Vec<i8>>> {
    if !pack.is_enabled() || query.trim().is_empty() {
        return Ok(None);
    }
    let embedder = load_embedder(data_dir, pack)?;
    Ok(embedder.encode_text(query, DEFAULT_MAX_TOKENS))
}

pub fn encode_document(
    embedder: &StaticEmbedder,
    name: &str,
    rel: &str,
    body: &str,
) -> Option<Vec<i8>> {
    let effective_name = if name.is_empty() {
        rel.rsplit(['\\', '/']).next().unwrap_or(rel)
    } else {
        name
    };
    let body_prefix = match body.char_indices().nth(BODY_PREFIX_CHARS) {
        Some((byte_idx, _)) => &body[..byte_idx],
        None => body,
    };
    let composite = format!("{effective_name} {effective_name} {rel}\n{body_prefix}");
    embedder.encode_text(&composite, DEFAULT_MAX_TOKENS)
}

pub fn ensure_vector_schema(db: &Connection) -> rusqlite::Result<()> {
    db.execute_batch(VECTOR_SCHEMA)
}

pub fn vector_count(db: &Connection, pack: SemanticPack) -> u64 {
    if !pack.is_enabled() {
        return 0;
    }
    db.query_row(
        "SELECT count(*) FROM content_vectors WHERE pack = ?1",
        [pack.id()],
        |r| r.get::<_, u64>(0),
    )
    .unwrap_or(0)
}

pub fn upsert_vector(
    db: &Connection,
    file_id: i64,
    pack: SemanticPack,
    vec: &[i8],
    now: i64,
) -> rusqlite::Result<()> {
    let raw_bytes = i8_as_u8(vec);
    db.prepare_cached(
        "INSERT OR REPLACE INTO content_vectors(file_id, pack, dims, vec, updated_at)
         VALUES(?1, ?2, ?3, ?4, ?5)",
    )?
    .execute(params![file_id, pack.id(), vec.len() as i64, raw_bytes, now])?;
    Ok(())
}

pub fn delete_vectors_for_volume(db: &Connection, volume_id: &str) -> rusqlite::Result<()> {
    let has_vectors: bool = db.query_row(
        "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='content_vectors')",
        [],
        |r| r.get(0),
    )?;
    if !has_vectors {
        return Ok(());
    }
    let has_content_files: bool = db.query_row(
        "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='content_files')",
        [],
        |r| r.get(0),
    )?;
    if has_content_files {
        db.execute(
            "DELETE FROM content_vectors
             WHERE file_id IN (SELECT file_id FROM content_files WHERE volume = ?1)
                OR NOT EXISTS (SELECT 1 FROM content_files c WHERE c.file_id = content_vectors.file_id)",
            [volume_id],
        )?;
    }
    Ok(())
}

pub fn backfill_from_index(
    catalog: &Catalog,
    pack: SemanticPack,
    cancel: &AtomicBool,
    mut on_progress: impl FnMut(u64, u64),
) -> EngineResult<u64> {
    check_cancel(cancel)?;
    if !pack.is_enabled() {
        return Ok(0);
    }

    let embedder = load_embedder(&catalog.data_dir, pack)?;
    let dims = embedder.dims() as i64;
    let pack_id = pack.id();

    catalog.open()?;
    fs::create_dir_all(&catalog.data_dir)?;
    let mut db = Connection::open(catalog.data_dir.join("content.sqlite3"))?;
    configure(&db)?;
    db.execute_batch(crate::content::SCHEMA)?;
    db.execute_batch(VECTOR_SCHEMA)?;
    db.execute(
        "ATTACH DATABASE ?1 AS source_catalog",
        [catalog.database_path().to_string_lossy().as_ref()],
    )?;

    let missing_ids: Vec<i64> = {
        let mut stmt = db.prepare(
            "SELECT c.file_id
             FROM content_files c
             LEFT JOIN content_vectors v
               ON v.file_id = c.file_id AND v.pack = ?1 AND v.dims = ?2
             WHERE c.status = 1 AND c.brotli_text IS NOT NULL AND v.file_id IS NULL
             ORDER BY c.file_id",
        )?;
        let rows = stmt.query_map(params![pack_id, dims], |r| r.get::<_, i64>(0))?;
        rows.collect::<rusqlite::Result<Vec<_>>>()?
    };

    let total = missing_ids.len() as u64;
    on_progress(0, total);
    if total == 0 {
        return Ok(0);
    }

    let mut processed = 0u64;
    let mut embedded = 0u64;

    for chunk in missing_ids.chunks(BACKFILL_BATCH) {
        check_cancel(cancel)?;
        let chunk_json = serde_json::to_string(chunk)?;
        let sources: Vec<(i64, String, String, Vec<u8>)> = {
            let mut stmt = db.prepare_cached(
                "SELECT c.file_id, coalesce(f.name, ''), c.rel, c.brotli_text
                 FROM content_files c
                 LEFT JOIN source_catalog.files f ON f.id = c.file_id
                 WHERE c.file_id IN (SELECT value FROM json_each(?1))
                   AND c.status = 1 AND c.brotli_text IS NOT NULL
                 ORDER BY c.file_id",
            )?;
            let rows = stmt.query_map([chunk_json], |r| {
                Ok((
                    r.get::<_, i64>(0)?,
                    r.get::<_, String>(1)?,
                    r.get::<_, String>(2)?,
                    r.get::<_, Vec<u8>>(3)?,
                ))
            })?;
            rows.collect::<rusqlite::Result<Vec<_>>>()?
        };

        let mut encoded_batch = Vec::with_capacity(sources.len());
        for (file_id, name, rel, compressed) in sources {
            check_cancel(cancel)?;
            if let Ok(body_prefix) = decompress_prefix(&compressed) {
                if let Some(vec) = encode_document(&embedder, &name, &rel, &body_prefix) {
                    encoded_batch.push((file_id, vec));
                }
            }
        }

        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let stamp = now_ms();
        for (file_id, vec) in &encoded_batch {
            upsert_vector(&tx, *file_id, pack, vec, stamp)?;
            embedded += 1;
        }
        tx.commit()?;

        processed = processed.saturating_add(chunk.len() as u64).min(total);
        on_progress(processed, total);
    }

    Ok(embedded)
}

#[derive(Copy, Clone, Debug)]
struct RankedVectorHit {
    cosine: f32,
    file_id: i64,
}

impl PartialEq for RankedVectorHit {
    fn eq(&self, other: &Self) -> bool {
        self.cosine.to_bits() == other.cosine.to_bits() && self.file_id == other.file_id
    }
}

impl Eq for RankedVectorHit {}

impl PartialOrd for RankedVectorHit {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for RankedVectorHit {
    fn cmp(&self, other: &Self) -> Ordering {
        // Reverse ordering so BinaryHeap::peek() exposes the weakest kept hit (min-heap).
        other
            .cosine
            .total_cmp(&self.cosine)
            .then_with(|| self.file_id.cmp(&other.file_id))
    }
}

pub fn search_vectors(
    db: &Connection,
    pack: SemanticPack,
    query_vec: &[i8],
    min_cosine: f32,
    limit: usize,
) -> rusqlite::Result<Vec<(i64, f32)>> {
    if !pack.is_enabled() || query_vec.is_empty() || limit == 0 {
        return Ok(Vec::new());
    }

    let query_norm_sq: i32 = query_vec.iter().map(|&x| (x as i32) * (x as i32)).sum();
    if query_norm_sq <= 0 {
        return Ok(Vec::new());
    }

    let has_table: bool = db.query_row(
        "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='content_vectors')",
        [],
        |r| r.get(0),
    )?;
    if !has_table {
        return Ok(Vec::new());
    }

    let dims = query_vec.len();
    let q_norm_f = query_norm_sq as f32;
    let mut heap: BinaryHeap<RankedVectorHit> = BinaryHeap::with_capacity(limit.min(1024) + 1);

    let mut stmt = db.prepare_cached(
        "SELECT file_id, vec FROM content_vectors WHERE pack = ?1 AND dims = ?2",
    )?;
    let mut rows = stmt.query(params![pack.id(), dims as i64])?;

    while let Some(row) = rows.next()? {
        let file_id: i64 = row.get(0)?;
        let blob = match row.get_ref(1)? {
            ValueRef::Blob(b) => b,
            _ => continue,
        };
        if blob.len() != dims {
            continue;
        }

        let (dot, cand_norm_sq) = dot_and_norm_unrolled8(query_vec, u8_as_i8(blob));
        if cand_norm_sq <= 0 || (min_cosine > 0.0 && dot <= 0) {
            continue;
        }

        let denom = (q_norm_f * (cand_norm_sq as f32)).sqrt();
        if denom <= 0.0 {
            continue;
        }
        let cosine = ((dot as f32) / denom).clamp(-1.0, 1.0);
        if cosine < min_cosine {
            continue;
        }

        let candidate = RankedVectorHit { cosine, file_id };
        if heap.len() < limit {
            heap.push(candidate);
        } else if let Some(&worst) = heap.peek() {
            if candidate < worst {
                heap.pop();
                heap.push(candidate);
            }
        }
    }

    let mut results: Vec<RankedVectorHit> = heap.into_vec();
    results.sort_unstable_by(|a, b| {
        b.cosine
            .total_cmp(&a.cosine)
            .then_with(|| a.file_id.cmp(&b.file_id))
    });

    Ok(results
        .into_iter()
        .map(|hit| (hit.file_id, hit.cosine))
        .collect())
}

#[inline]
fn dot_and_norm_unrolled8(q: &[i8], c: &[i8]) -> (i32, i32) {
    let mut dot_a = 0i32;
    let mut dot_b = 0i32;
    let mut norm_a = 0i32;
    let mut norm_b = 0i32;

    let mut q_chunks = q.chunks_exact(8);
    let mut c_chunks = c.chunks_exact(8);

    for (qc, cc) in q_chunks.by_ref().zip(c_chunks.by_ref()) {
        let q0 = qc[0] as i32;
        let c0 = cc[0] as i32;
        let q1 = qc[1] as i32;
        let c1 = cc[1] as i32;
        let q2 = qc[2] as i32;
        let c2 = cc[2] as i32;
        let q3 = qc[3] as i32;
        let c3 = cc[3] as i32;
        let q4 = qc[4] as i32;
        let c4 = cc[4] as i32;
        let q5 = qc[5] as i32;
        let c5 = cc[5] as i32;
        let q6 = qc[6] as i32;
        let c6 = cc[6] as i32;
        let q7 = qc[7] as i32;
        let c7 = cc[7] as i32;

        dot_a += q0 * c0 + q1 * c1 + q2 * c2 + q3 * c3;
        dot_b += q4 * c4 + q5 * c5 + q6 * c6 + q7 * c7;
        norm_a += c0 * c0 + c1 * c1 + c2 * c2 + c3 * c3;
        norm_b += c4 * c4 + c5 * c5 + c6 * c6 + c7 * c7;
    }

    let mut dot = dot_a + dot_b;
    let mut norm_sq = norm_a + norm_b;
    for (&qv, &cv) in q_chunks.remainder().iter().zip(c_chunks.remainder().iter()) {
        let qi = qv as i32;
        let ci = cv as i32;
        dot += qi * ci;
        norm_sq += ci * ci;
    }
    (dot, norm_sq)
}

#[inline]
fn i8_as_u8(slice: &[i8]) -> &[u8] {
    // SAFETY: i8 and u8 share identical size (1), alignment (1), and bit validity.
    unsafe { std::slice::from_raw_parts(slice.as_ptr().cast::<u8>(), slice.len()) }
}

#[inline]
fn u8_as_i8(slice: &[u8]) -> &[i8] {
    // SAFETY: u8 and i8 share identical size (1), alignment (1), and bit validity.
    unsafe { std::slice::from_raw_parts(slice.as_ptr().cast::<i8>(), slice.len()) }
}

fn decompress_prefix(compressed: &[u8]) -> io::Result<String> {
    let mut raw = Vec::with_capacity(8_192);
    brotli::Decompressor::new(compressed, 4096)
        .take(BODY_DECOMPRESS_LIMIT)
        .read_to_end(&mut raw)?;
    match String::from_utf8(raw) {
        Ok(mut text) => {
            if let Some((idx, _)) = text.char_indices().nth(BODY_PREFIX_CHARS) {
                text.truncate(idx);
            }
            Ok(text)
        }
        Err(err) if err.utf8_error().error_len().is_none() => {
            let valid_len = err.utf8_error().valid_up_to();
            let mut bytes = err.into_bytes();
            bytes.truncate(valid_len);
            let mut text = String::from_utf8(bytes).map_err(|e| invalid(e.to_string()))?;
            if let Some((idx, _)) = text.char_indices().nth(BODY_PREFIX_CHARS) {
                text.truncate(idx);
            }
            Ok(text)
        }
        Err(err) => Err(invalid(err.to_string())),
    }
}

#[inline]
fn check_cancel(cancel: &AtomicBool) -> io::Result<()> {
    if cancel.load(AtomicOrdering::Relaxed) {
        Err(interrupted())
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pack_archive_requires_allowlisted_root_files() {
        let temp = std::env::temp_dir().join(format!(
            "file-inventory-pack-test-{:032x}",
            rand::random::<u128>()
        ));
        let output = temp.join("files");
        fs::create_dir_all(&output).unwrap();
        let cancel = AtomicBool::new(false);

        for (model_name, accepted) in [
            ("model.safetensors", true),
            ("../model.safetensors", false),
            ("nested/model.safetensors", false),
            ("nested\\model.safetensors", false),
            ("MODEL.SAFETENSORS", false),
        ] {
            let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
            for name in ["config.json", "tokenizer.json", model_name] {
                zip.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
                zip.write_all(b"fixture").unwrap();
            }
            let mut archive = zip.finish().unwrap();
            archive.set_position(0);
            assert_eq!(extract_pack(archive, &output, &cancel).is_ok(), accepted);
            assert!(!temp.join("model.safetensors").exists());
        }
        fs::remove_dir_all(&temp).unwrap();
    }

    fn f32_to_f16_bits(val: f32) -> u16 {
        let bits = val.to_bits();
        let sign = ((bits >> 16) & 0x8000) as u16;
        let exp = ((bits >> 23) & 0xff) as i32;
        let mant = bits & 0x007f_ffff;
        if exp == 0xff {
            return sign | 0x7c00 | if mant != 0 { 0x0200 } else { 0 };
        }
        let half_exp = exp - 127 + 15;
        if half_exp >= 31 {
            sign | 0x7c00
        } else if half_exp <= 0 {
            sign
        } else {
            sign | ((half_exp as u16) << 10) | ((mant >> 13) as u16)
        }
    }

    fn build_synthetic_safetensors(rows: &[Vec<f32>], dtype: &str) -> Vec<u8> {
        let vocab_size = rows.len();
        let dims = rows[0].len();
        let mut payload = Vec::new();
        for row in rows {
            assert_eq!(row.len(), dims);
            for &v in row {
                match dtype {
                    "F32" => payload.extend_from_slice(&v.to_le_bytes()),
                    "F16" => payload.extend_from_slice(&f32_to_f16_bits(v).to_le_bytes()),
                    "BF16" => payload.extend_from_slice(&((v.to_bits() >> 16) as u16).to_le_bytes()),
                    other => panic!("unsupported test dtype {other}"),
                }
            }
        }
        let header = serde_json::json!({
            "__metadata__": { "format": "pt" },
            "embeddings": {
                "dtype": dtype,
                "shape": [vocab_size, dims],
                "data_offsets": [0, payload.len()]
            }
        });
        let header_bytes = serde_json::to_vec(&header).unwrap();
        let mut out = Vec::with_capacity(8 + header_bytes.len() + payload.len());
        out.extend_from_slice(&(header_bytes.len() as u64).to_le_bytes());
        out.extend_from_slice(&header_bytes);
        out.extend_from_slice(&payload);
        out
    }

    fn sample_embedding_rows() -> Vec<Vec<f32>> {
        vec![
            vec![0.0; 8],                                              // 0: [PAD] / <pad>
            vec![0.0; 8],                                              // 1: [UNK] / <unk>
            vec![0.02, 0.02, 0.00, 0.00, 0.02, 0.00, 0.00, 0.00],      // 2: a
            vec![0.02, 0.02, 0.00, 0.00, 0.02, 0.00, 0.00, 0.00],      // 3: an
            vec![0.95, 0.85, 0.15, 0.10, -0.10, 0.00, 0.00, 0.00],     // 4: buy
            vec![0.15, 0.10, 0.05, 0.00, 0.00, 0.00, 0.00, 0.00],      // 5: ##ing / ing
            vec![0.92, 0.88, 0.12, 0.12, -0.08, 0.00, 0.00, 0.00],     // 6: purchas
            vec![0.20, 0.15, 0.96, 0.90, -0.12, 0.00, 0.00, 0.00],     // 7: car
            vec![0.18, 0.16, 0.94, 0.92, -0.10, 0.00, 0.00, 0.00],     // 8: automobile
            vec![-0.05, 0.00, 0.00, 0.00, 0.90, 0.85, 0.20, 0.15],     // 9: quarterly
            vec![-0.08, 0.00, 0.00, 0.00, 0.88, 0.92, 0.25, 0.10],     // 10: tax
            vec![0.00, -0.05, 0.00, 0.00, 0.25, 0.20, 0.95, 0.90],     // 11: spreadsheet
        ]
    }

    #[test]
    fn ieee_half_and_bf16_conversions_are_exact() {
        assert_eq!(f16_to_f32(0x0000), 0.0);
        assert!(f16_to_f32(0x8000).is_sign_negative() && f16_to_f32(0x8000) == 0.0);
        assert!((f16_to_f32(0x3c00) - 1.0).abs() < 1e-6);
        assert!((f16_to_f32(0xc000) + 2.0).abs() < 1e-6);
        assert!((f16_to_f32(0x0001) - 2.0f32.powi(-24)).abs() < 1e-12);
        assert!(f16_to_f32(0x7c00).is_infinite());
        assert!(f16_to_f32(0x7e00).is_nan());
        assert!((bf16_to_f32(0x3f80) - 1.0).abs() < 1e-6);
        assert!((bf16_to_f32(0xc040) + 3.0).abs() < 1e-6);
    }

    #[test]
    fn wordpiece_f32_and_unigram_f16_roundtrip_and_semantic_ranking() {
        let rows = sample_embedding_rows();

        // 1. WordPiece object vocab + F32 SafeTensors
        let wp_tokenizer = serde_json::json!({
            "normalizer": { "type": "BertNormalizer", "lowercase": true, "strip_accents": true },
            "model": {
                "type": "WordPiece",
                "unk_token": "[UNK]",
                "continuing_subword_prefix": "##",
                "vocab": {
                    "[PAD]": 0,
                    "[UNK]": 1,
                    "a": 2,
                    "an": 3,
                    "buy": 4,
                    "##ing": 5,
                    "purchas": 6,
                    "car": 7,
                    "automobile": 8,
                    "quarterly": 9,
                    "tax": 10,
                    "spreadsheet": 11
                }
            }
        });
        let f32_bytes = build_synthetic_safetensors(&rows, "F32");
        let wp_embedder =
            StaticEmbedder::from_bytes(&f32_bytes, &serde_json::to_vec(&wp_tokenizer).unwrap())
                .expect("WordPiece + F32 embedder should load");
        assert_eq!(wp_embedder.dims(), 8);
        assert_eq!(wp_embedder.vocab_size(), 12);
        assert_eq!(wp_embedder.tokenize("Buying a car!", 32), vec![4, 5, 2, 7]);
        assert_eq!(
            wp_embedder.tokenize("Purchasing an automobile", 32),
            vec![6, 5, 3, 8]
        );

        let q_vec = wp_embedder
            .encode_text("buying a car", DEFAULT_MAX_TOKENS)
            .expect("query vector");
        let auto_vec = wp_embedder
            .encode_text("purchasing an automobile", DEFAULT_MAX_TOKENS)
            .expect("auto vector");
        let tax_vec = wp_embedder
            .encode_text("quarterly tax spreadsheet", DEFAULT_MAX_TOKENS)
            .expect("tax vector");

        let db = Connection::open_in_memory().unwrap();
        ensure_vector_schema(&db).unwrap();
        upsert_vector(&db, 101, SemanticPack::EnglishFast, &tax_vec, 1_000).unwrap();
        upsert_vector(&db, 202, SemanticPack::EnglishFast, &auto_vec, 1_000).unwrap();
        assert_eq!(vector_count(&db, SemanticPack::EnglishFast), 2);
        assert_eq!(vector_count(&db, SemanticPack::Multilingual), 0);

        let hits = search_vectors(
            &db,
            SemanticPack::EnglishFast,
            &q_vec,
            DEFAULT_MIN_COSINE,
            10,
        )
        .unwrap();
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].0, 202);
        assert!(
            hits[0].1 > 0.95,
            "expected high cosine similarity for synonymous doc, got {}",
            hits[0].1
        );

        // 2. Unigram array vocab + F16 SafeTensors
        let unigram_tokenizer = serde_json::json!({
            "normalizer": { "type": "Sequence", "normalizers": [{ "type": "Lowercase" }] },
            "model": {
                "type": "Unigram",
                "unk_id": 1,
                "vocab": [
                    ["<pad>", 0.0],
                    ["<unk>", 0.0],
                    ["\u{2581}a", -2.0],
                    ["\u{2581}an", -2.5],
                    ["\u{2581}buy", -3.1],
                    ["ing", -2.2],
                    ["\u{2581}purchas", -3.4],
                    ["\u{2581}car", -3.0],
                    ["\u{2581}automobile", -3.8],
                    ["\u{2581}quarterly", -4.0],
                    ["\u{2581}tax", -3.5],
                    ["\u{2581}spreadsheet", -4.2]
                ]
            }
        });
        let f16_bytes = build_synthetic_safetensors(&rows, "F16");
        let uni_embedder = StaticEmbedder::from_bytes(
            &f16_bytes,
            &serde_json::to_vec(&unigram_tokenizer).unwrap(),
        )
        .expect("Unigram + F16 embedder should load");

        assert_eq!(uni_embedder.tokenize("buying a car", 32), vec![4, 5, 2, 7]);
        let uni_q = uni_embedder.encode_text("buying a car", 128).unwrap();
        let uni_auto = uni_embedder
            .encode_text("purchasing an automobile", 128)
            .unwrap();
        let uni_tax = uni_embedder
            .encode_text("quarterly tax spreadsheet", 128)
            .unwrap();

        upsert_vector(&db, 301, SemanticPack::Multilingual, &uni_tax, 2_000).unwrap();
        upsert_vector(&db, 302, SemanticPack::Multilingual, &uni_auto, 2_000).unwrap();

        let uni_hits = search_vectors(
            &db,
            SemanticPack::Multilingual,
            &uni_q,
            DEFAULT_MIN_COSINE,
            10,
        )
        .unwrap();
        assert_eq!(uni_hits.len(), 1);
        assert_eq!(uni_hits[0].0, 302);
        assert!(uni_hits[0].1 > 0.95);
    }

    #[test]
    fn settings_pack_info_and_backfill_roundtrip() {
        let temp_dir = std::env::temp_dir().join(format!(
            "fi_semantic_test_{:032x}",
            rand::random::<u128>()
        ));
        fs::create_dir_all(&temp_dir).unwrap();

        assert_eq!(load_pack_setting(&temp_dir), SemanticPack::Off);
        save_pack_setting(&temp_dir, SemanticPack::EnglishFast).unwrap();
        assert_eq!(load_pack_setting(&temp_dir), SemanticPack::EnglishFast);

        let model_dir = pack_dir(&temp_dir, SemanticPack::EnglishFast).unwrap();
        fs::create_dir_all(&model_dir).unwrap();
        let rows = sample_embedding_rows();
        let safetensors = build_synthetic_safetensors(&rows, "F32");
        let tokenizer = serde_json::json!({
            "normalizer": { "type": "BertNormalizer", "lowercase": true },
            "model": {
                "type": "WordPiece",
                "unk_token": "[UNK]",
                "vocab": {
                    "[PAD]": 0, "[UNK]": 1, "a": 2, "an": 3, "buy": 4, "##ing": 5,
                    "purchas": 6, "car": 7, "automobile": 8, "quarterly": 9,
                    "tax": 10, "spreadsheet": 11
                }
            }
        });
        fs::write(model_dir.join("model.safetensors"), &safetensors).unwrap();
        fs::write(
            model_dir.join("tokenizer.json"),
            serde_json::to_vec(&tokenizer).unwrap(),
        )
        .unwrap();

        let info = pack_info(&temp_dir, SemanticPack::EnglishFast);
        assert!(info.installed);
        assert!(info.disk_bytes > 0);

        let catalog = Catalog::new(&temp_dir);
        {
            let cat_db = catalog.open().unwrap();
            cat_db
                .execute(
                    "INSERT INTO volumes(id, mount, label, online) VALUES('vol1', 'C:\\', 'System', 1)",
                    [],
                )
                .unwrap();
            cat_db
                .execute(
                    "INSERT INTO files(id, volume, rel, name, name_key, parent_key, extension, bytes, modified, created, attributes, seen)
                     VALUES(1, 'vol1', 'docs\\deal.txt', 'deal.txt', 'deal.txt', 'docs', 'txt', 32, 100, 100, 32, 100),
                           (2, 'vol1', 'finance\\q4.txt', 'q4.txt', 'q4.txt', 'finance', 'txt', 32, 100, 100, 32, 100)",
                    [],
                )
                .unwrap();
        }
        {
            let content_db = Connection::open(temp_dir.join("content.sqlite3")).unwrap();
            content_db.execute_batch(crate::content::SCHEMA).unwrap();
            let compress_str = |s: &str| {
                let mut enc = brotli::CompressorWriter::new(Vec::new(), 4096, 4, 21);
                enc.write_all(s.as_bytes()).unwrap();
                enc.into_inner()
            };
            content_db
                .execute(
                    "INSERT INTO content_files VALUES(1, 'vol1', 'docs\\deal.txt', 32, 100, 1, 1, 100, ?1)",
                    [compress_str("purchasing an automobile")],
                )
                .unwrap();
            content_db
                .execute(
                    "INSERT INTO content_files VALUES(2, 'vol1', 'finance\\q4.txt', 32, 100, 1, 1, 100, ?1)",
                    [compress_str("quarterly tax spreadsheet")],
                )
                .unwrap();
        }

        let cancel = AtomicBool::new(false);
        let mut last_progress = (0, 0);
        let embedded = backfill_from_index(&catalog, SemanticPack::EnglishFast, &cancel, |done, total| {
            last_progress = (done, total);
        })
        .unwrap();
        assert_eq!(embedded, 2);
        assert_eq!(last_progress, (2, 2));

        let q_vec = encode_query(&temp_dir, SemanticPack::EnglishFast, "buying a car")
            .unwrap()
            .unwrap();
        let content_db = Connection::open(temp_dir.join("content.sqlite3")).unwrap();
        let hits = search_vectors(
            &content_db,
            SemanticPack::EnglishFast,
            &q_vec,
            DEFAULT_MIN_COSINE,
            5,
        )
        .unwrap();
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].0, 1);
        assert!(hits[0].1 > 0.90);

        delete_vectors_for_volume(&content_db, "vol1").unwrap();
        assert_eq!(vector_count(&content_db, SemanticPack::EnglishFast), 0);
        drop(content_db);

        remove_pack(&temp_dir, SemanticPack::EnglishFast).unwrap();
        assert!(!pack_info(&temp_dir, SemanticPack::EnglishFast).installed);
        let _ = fs::remove_dir_all(&temp_dir);
    }
}
