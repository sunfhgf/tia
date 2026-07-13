const { app, BrowserWindow } = require('electron');
const { spawn } = require('child_process');
const path = require('path');
const http = require('http');

const API_PORT = 5005;
const API_URL = `http://127.0.0.1:${API_PORT}`;
let apiProcess = null;
let mainWindow = null;

function startApi() {
  const exe = path.resolve(__dirname, '..', 'TiaAutomation', 'src', 'TiaAutomation.Api', 'bin', 'Release', 'net48', 'TiaAutomation.Api.exe');
  console.log('[main] spawning api:', exe);
  apiProcess = spawn(exe, ['--port', String(API_PORT)], {
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: false,
  });
  apiProcess.stdout.on('data', d => console.log('[api]', d.toString().trim()));
  apiProcess.stderr.on('data', d => console.error('[api]', d.toString().trim()));
  apiProcess.on('exit', code => console.log('[api] exited', code));
}

function waitForApi(maxMs = 15000) {
  return new Promise((resolve, reject) => {
    const start = Date.now();
    const poll = () => {
      http.get(`${API_URL}/api/health`, res => {
        let body = '';
        res.on('data', c => body += c);
        res.on('end', () => {
          try { const j = JSON.parse(body); if (j.ok) return resolve(j); } catch {}
          retry(start, maxMs, poll, reject);
        });
      }).on('error', () => retry(start, maxMs, poll, reject));
    };
    poll();
  });
}

function retry(start, maxMs, poll, reject) {
  if (Date.now() - start > maxMs) return reject(new Error('API health timeout'));
  setTimeout(poll, 500);
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1200,
    height: 800,
    title: 'TIA 自动化',
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
      preload: path.join(__dirname, 'preload.js'),
    },
  });
  mainWindow.loadFile(path.join(__dirname, 'renderer', 'index.html'));
  mainWindow.setMenuBarVisibility(false);
  if (process.env.TIA_AUTOMATION_DEVTOOLS === '1') {
    mainWindow.webContents.openDevTools();
  }
  mainWindow.on('closed', () => { mainWindow = null; });
}

app.whenReady().then(async () => {
  startApi();
  try {
    const health = await waitForApi();
    console.log('[main] api ready:', health.version);
  } catch (e) {
    console.error('[main] api not ready:', e.message);
  }
  createWindow();
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('before-quit', () => {
  if (apiProcess) {
    console.log('[main] killing api process');
    apiProcess.kill();
    apiProcess = null;
  }
});

