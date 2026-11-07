// Runs the UI tests against the web demo: python3 tools/pages/build.py builds it, a static server serves it.
const { defineConfig } = require('@playwright/test');
const path = require('path');

const root = path.resolve(__dirname, '..', '..');
const site = path.join(root, '.ui-site');
const python = process.platform === 'win32' ? 'python' : 'python3';

module.exports = defineConfig({
  testDir: __dirname,
  timeout: 30_000,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never', outputFolder: path.join(root, 'ui-test-report') }]] : 'list',
  use: {
    baseURL: 'http://127.0.0.1:8781/',
    viewport: { width: 780, height: 360 },
    trace: 'retain-on-failure',
  },
  webServer: {
    command: `${python} "${path.join(root, 'tools', 'pages', 'build.py')}" "${site}" && ${python} -m http.server 8781 --bind 127.0.0.1 --directory "${site}"`,
    url: 'http://127.0.0.1:8781/panel/',
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
  },
});
