(() => {
  const themeSelect = document.querySelector('#themeSelect');
  if (!themeSelect) return;

  const themeKey = 'hello-ollama-theme';
  const validThemes = ['dark', 'light', 'system'];
  const systemTheme = window.matchMedia('(prefers-color-scheme: light)');
  const themeColor = document.querySelector('meta[name="theme-color"]');
  let preference = 'dark';

  try {
    const savedTheme = localStorage.getItem(themeKey);
    if (validThemes.includes(savedTheme)) preference = savedTheme;
  } catch (error) {
    console.warn('Could not load the saved theme preference.', error);
  }

  const applyTheme = () => {
    const theme = preference === 'system'
      ? (systemTheme.matches ? 'light' : 'dark')
      : preference;
    document.documentElement.dataset.theme = theme;
    if (themeColor) themeColor.content = theme === 'light' ? '#f4f6f2' : '#101112';
  };

  themeSelect.value = preference;
  applyTheme();
  themeSelect.addEventListener('change', () => {
    preference = themeSelect.value;
    applyTheme();
    try {
      localStorage.setItem(themeKey, preference);
    } catch (error) {
      console.warn('Could not save the theme preference.', error);
    }
  });
  systemTheme.addEventListener('change', () => {
    if (preference === 'system') applyTheme();
  });
})();
