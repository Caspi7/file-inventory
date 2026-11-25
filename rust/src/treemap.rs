use file_inventory::{catalog::StorageMapItem, util::format_bytes};

#[derive(Debug)]
pub struct TreemapRect {
    pub x: f64,
    pub y: f64,
    pub w: f64,
    pub h: f64,
    pub label: String,
    pub size_text: String,
    pub rel: String,
    pub is_dir: bool,
    pub color_idx: usize,
}

pub fn layout(mut items: Vec<StorageMapItem>, width: f64, height: f64) -> Vec<TreemapRect> {
    items.retain(|item| item.bytes > 0);
    items.sort_by(|a, b| b.bytes.cmp(&a.bytes).then_with(|| a.rel.cmp(&b.rel)));
    let total = items.iter().map(|item| item.bytes as f64).sum::<f64>();
    if items.is_empty() || width <= 0.0 || height <= 0.0 {
        return Vec::new();
    }
    let scale = width * height / total;
    let (mut x, mut y, mut w, mut h) = (0.0, 0.0, width, height);
    let mut tiles = Vec::with_capacity(items.len());
    let mut start = 0;
    while start < items.len() {
        let sideways = w >= h;
        let edge = if sideways { h } else { w };
        if edge <= 0.0 {
            break;
        }
        let largest = items[start].bytes as f64 * scale;
        let mut sum = largest;
        let mut end = start + 1;
        let worst = |sum: f64, smallest: f64| {
            let ratio = (sum / edge).powi(2);
            (ratio / smallest).max(largest / ratio)
        };
        while end < items.len() {
            let next = items[end].bytes as f64 * scale;
            if worst(sum + next, next) > worst(sum, items[end - 1].bytes as f64 * scale) {
                break;
            }
            sum += next;
            end += 1;
        }
        let thickness = sum / edge;
        let mut offset = 0.0;
        for item in &items[start..end] {
            let length = item.bytes as f64 * scale / thickness;
            let (left, top, dx, dy) = if sideways {
                (x, y + offset, thickness, length)
            } else {
                (x + offset, y, length, thickness)
            };
            tiles.push(TreemapRect {
                x: left / width, y: top / height, w: dx / width, h: dy / height,
                label: item.name.clone(),
                size_text: format!("{} · {} · {} files", item.name, format_bytes(item.bytes as u64), item.file_count),
                rel: item.rel.clone(), is_dir: item.is_directory,
                color_idx: if item.is_directory { 0 } else {
                    1 + item.extension.bytes().fold(0usize, |hash, byte| (hash * 31 + usize::from(byte)) % 5)
                },
            });
            offset += length;
        }
        if sideways { x += thickness; w -= thickness; } else { y += thickness; h -= thickness; }
        start = end;
    }
    tiles
}
