const fs = require('node:fs');
const path = require('node:path');
let playwrightExecutablePath = null;

try {
  const { chromium } = require('playwright');
  playwrightExecutablePath = chromium.executablePath();
} catch {
  playwrightExecutablePath = null;
}

const browserCandidates = [
  process.env.CHROME_BIN,
  playwrightExecutablePath,
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
  '/usr/bin/google-chrome',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
].filter(Boolean);

const detectedBrowser = browserCandidates.find((candidate) => fs.existsSync(candidate));
if (detectedBrowser) {
  process.env.CHROME_BIN = detectedBrowser;
}

module.exports = function configure(config) {
  const singleRun = process.env.KARMA_WATCH !== 'true';
  config.set({
    basePath: '',
    frameworks: ['jasmine'],
    plugins: [
      require('karma-jasmine'),
      require('karma-chrome-launcher'),
      require('karma-jasmine-html-reporter'),
      require('karma-coverage'),
    ],
    client: {
      clearContext: false,
      jasmine: {
        random: false,
      },
    },
    jasmineHtmlReporter: {
      suppressAll: true,
    },
    coverageReporter: {
      dir: path.join(__dirname, 'coverage', 'frontend'),
      subdir: '.',
      reporters: [{ type: 'html' }, { type: 'text-summary' }],
    },
    reporters: ['progress', 'kjhtml'],
    browsers: ['ChromeHeadlessCI'],
    customLaunchers: {
      ChromeHeadlessCI: {
        base: 'ChromeHeadless',
        flags: ['--disable-gpu', '--disable-dev-shm-usage', '--no-sandbox'],
      },
    },
    autoWatch: !singleRun,
    singleRun,
    browserDisconnectTolerance: 2,
    processKillTimeout: 10000,
    browserNoActivityTimeout: 120000,
    restartOnFileChange: !singleRun,
  });
};
