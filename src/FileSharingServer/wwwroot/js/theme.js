(function () {
    var stored = localStorage.getItem('theme');
    if (stored === 'dark' || (!stored && window.matchMedia('(prefers-color-scheme: dark)').matches)) {
        document.documentElement.classList.add('dark-theme');
    }
})();

function toggleTheme() {
    var isDark = document.documentElement.classList.toggle('dark-theme');
    localStorage.setItem('theme', isDark ? 'dark' : 'light');
    updateThemeLabel();
}

function updateThemeLabel() {
    var el = document.getElementById('theme-label');
    if (!el) return;
    var isDark = document.documentElement.classList.contains('dark-theme');
    el.textContent = isDark ? '\u263D Dark' : '\u2600 Light';
}

document.addEventListener('DOMContentLoaded', updateThemeLabel);
