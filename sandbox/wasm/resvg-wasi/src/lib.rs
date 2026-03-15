use std::alloc::{alloc, dealloc, Layout};

/// Embedded Liberation Sans font — metrics compatible with Helvetica/Arial.
static FONT_DATA: &[u8] = include_bytes!("../LiberationSans-Regular.ttf");

/// Render SVG to PNG.
/// Takes SVG string (ptr + len), returns packed i64: (png_ptr << 32) | png_len.
/// Returns 0 on failure. Caller frees via `wasm_free`.
#[unsafe(no_mangle)]
pub extern "C" fn svg_to_png(svg_ptr: *const u8, svg_len: i32, width: i32) -> i64 {
    if svg_ptr.is_null() || svg_len <= 0 {
        return 0;
    }

    let svg_data = unsafe { std::slice::from_raw_parts(svg_ptr, svg_len as usize) };
    let svg_str = match std::str::from_utf8(svg_data) {
        Ok(s) => s,
        Err(_) => return 0,
    };

    // Set up font database with embedded font
    let mut fontdb = resvg::usvg::fontdb::Database::new();
    fontdb.load_font_data(FONT_DATA.to_vec());

    let face_count = fontdb.len();

    // Map ALL generic families to our embedded font
    fontdb.set_serif_family("Liberation Sans");
    fontdb.set_sans_serif_family("Liberation Sans");
    fontdb.set_monospace_family("Liberation Sans");
    fontdb.set_cursive_family("Liberation Sans");
    fontdb.set_fantasy_family("Liberation Sans");

    // If fontdb couldn't parse the font, return diagnostic as a 1x1 image
    // with the face count encoded in the error
    if face_count == 0 {
        // Return an error: pack a diagnostic message instead
        let msg = format!("FONT_LOAD_FAILED:faces={},data_len={}", face_count, FONT_DATA.len());
        let bytes = msg.as_bytes();
        let layout = match Layout::from_size_align(bytes.len(), 1) {
            Ok(l) => l,
            Err(_) => return 0,
        };
        let ptr = unsafe { alloc(layout) };
        if ptr.is_null() { return 0; }
        unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), ptr, bytes.len()); }
        return ((ptr as u32 as i64) << 32) | (bytes.len() as u32 as i64);
    }

    // Parse SVG with font database
    let opts = resvg::usvg::Options {
        fontdb: std::sync::Arc::new(fontdb),
        ..Default::default()
    };

    let tree = match resvg::usvg::Tree::from_str(svg_str, &opts) {
        Ok(t) => t,
        Err(_) => return 0,
    };

    // Determine output size
    let svg_size = tree.size();
    let (w, h) = if width > 0 {
        let scale = width as f32 / svg_size.width();
        (width as u32, (svg_size.height() * scale) as u32)
    } else {
        (svg_size.width() as u32, svg_size.height() as u32)
    };

    if w == 0 || h == 0 {
        return 0;
    }

    // Create pixel buffer
    let mut pixmap = match tiny_skia::Pixmap::new(w, h) {
        Some(p) => p,
        None => return 0,
    };

    // Render
    let transform = if width > 0 {
        let scale = width as f32 / svg_size.width();
        tiny_skia::Transform::from_scale(scale, scale)
    } else {
        tiny_skia::Transform::default()
    };

    resvg::render(&tree, transform, &mut pixmap.as_mut());

    // Encode to PNG
    let png_data = match pixmap.encode_png() {
        Ok(d) => d,
        Err(_) => return 0,
    };

    // Copy to WASM-allocated buffer
    let len = png_data.len();
    let layout = match Layout::from_size_align(len, 1) {
        Ok(l) => l,
        Err(_) => return 0,
    };

    let ptr = unsafe { alloc(layout) };
    if ptr.is_null() {
        return 0;
    }

    unsafe {
        std::ptr::copy_nonoverlapping(png_data.as_ptr(), ptr, len);
    }

    ((ptr as u32 as i64) << 32) | (len as u32 as i64)
}

#[unsafe(no_mangle)]
pub extern "C" fn wasm_alloc(size: i32) -> *mut u8 {
    if size <= 0 {
        return std::ptr::null_mut();
    }
    let layout = match Layout::from_size_align(size as usize, 1) {
        Ok(l) => l,
        Err(_) => return std::ptr::null_mut(),
    };
    unsafe { alloc(layout) }
}

#[unsafe(no_mangle)]
pub extern "C" fn wasm_free(ptr: *mut u8, size: i32) {
    if ptr.is_null() || size <= 0 {
        return;
    }
    let layout = match Layout::from_size_align(size as usize, 1) {
        Ok(l) => l,
        Err(_) => return,
    };
    unsafe { dealloc(ptr, layout) }
}
