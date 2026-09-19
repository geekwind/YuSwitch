// YuSwitch UI helpers: dynamic document title + favicon driven by saved settings.
window.egSetTitle = function (name, subtitle) {
    const base = name && name.trim() ? name.trim() : 'YuSwitch';
    // Keep the current page-specific suffix ("· 仪表盘" etc.) if present.
    const current = document.title;
    const idx = current.indexOf('·');
    let suffix = idx >= 0 ? current.substring(idx) : '';
    suffix = suffix.replace(/^·\s*/, '');           // normalize to "· xxx"
    document.title = base + (suffix ? ' · ' + suffix : (subtitle ? ' · ' + subtitle : ''));
};

window.egSetFavicon = function (logoType, logoValue, appName) {
    try {
        let link = document.querySelector("link[rel~='icon']");
        if (!link) {
            link = document.createElement('link');
            link.rel = 'icon';
            document.head.appendChild(link);
        }
        if (logoType === 'image' && logoValue) {
            link.href = logoValue;
            return;
        }
        // Emoji or monogram → draw to a canvas favicon.
        const size = 64;
        const c = document.createElement('canvas');
        c.width = c.height = size;
        const ctx = c.getContext('2d');
        // Rounded-square brand background.
        const r = 14;
        ctx.fillStyle = '#9184d9';
        ctx.beginPath();
        ctx.moveTo(r, 0);
        ctx.arcTo(size, 0, size, size, r);
        ctx.arcTo(size, size, 0, size, r);
        ctx.arcTo(0, size, 0, 0, r);
        ctx.arcTo(0, 0, size, 0, r);
        ctx.closePath();
        ctx.fill();
        ctx.fillStyle = '#ffffff';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        if (logoType === 'emoji' && logoValue) {
            ctx.font = '40px serif';
            ctx.fillText(logoValue, size / 2, size / 2 + 4);
        } else {
            const initials = (appName || 'YS').trim().substring(0, 2).toUpperCase();
            ctx.font = 'bold 30px "Segoe UI", sans-serif';
            ctx.fillText(initials, size / 2, size / 2 + 3);
        }
        link.href = c.toDataURL('image/png');
    } catch (e) { /* non-fatal */ }
};

// Read a file input as a data URL (for custom logo upload).
window.egReadFile = function (inputEl) {
    return new Promise((resolve, reject) => {
        const f = inputEl && inputEl.files && inputEl.files[0];
        if (!f) { resolve(''); return; }
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = reject;
        reader.readAsDataURL(f);
    });
};

// --- Theme (dark default / light), persisted in localStorage. ---
// Dark is the product default: <html data-theme> always gets a value,
// and app.css's :root tokens are the dark set (light is the override layer).
window.egGetTheme = function () {
    try { return localStorage.getItem('eg-theme') || 'dark'; }
    catch (e) { return 'dark'; }
};

window.egSetTheme = function (theme) {
    try { localStorage.setItem('eg-theme', theme); } catch (e) { /* private mode */ }
    document.documentElement.setAttribute('data-theme', theme);
};

// --- Sidebar rail (expanded 218px / collapsed 64px), persisted. Pure
// client-side: Blazor only renders the markup, this toggles <html data-rail>
// and app.css does the rest. ---
window.egToggleRail = function () {
    const collapsed = document.documentElement.getAttribute('data-rail') === 'collapsed';
    if (collapsed) {
        document.documentElement.removeAttribute('data-rail');
        try { localStorage.removeItem('eg-rail'); } catch (e) { }
    } else {
        document.documentElement.setAttribute('data-rail', 'collapsed');
        try { localStorage.setItem('eg-rail', 'collapsed'); } catch (e) { }
    }
};

// Whether the window is still near the top. Live views prepend new rows only
// in that case — otherwise prepending would shove content the user is reading.
window.egIsNearTop = function (px) {
    return (window.scrollY || window.pageYOffset || 0) <= (px || 160);
};

window.egScrollToTop = function () {
    window.scrollTo({ top: 0, behavior: 'smooth' });
};

// Apply the stored theme immediately on script load — before Blazor boots —
// so there is no flash of the wrong theme on refresh.
(function () {
    try {
        const t = localStorage.getItem('eg-theme') || 'dark';
        document.documentElement.setAttribute('data-theme', t);
        if (localStorage.getItem('eg-rail') === 'collapsed')
            document.documentElement.setAttribute('data-rail', 'collapsed');
    } catch (e) { /* ignore */ }
})();
