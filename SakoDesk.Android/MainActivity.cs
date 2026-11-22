using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;

namespace SakoDesk.AndroidApp;

[Activity(
    Label = "Sako Desk",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : Activity
{
    private WebView _webView;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestWindowFeature(WindowFeatures.NoTitle);

        _webView = new WebView(this);
        _webView.LayoutParameters = new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent);

        WebSettings settings = _webView.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.DatabaseEnabled = true;
        settings.AllowFileAccess = true;
        settings.AllowContentAccess = true;
        settings.MediaPlaybackRequiresUserGesture = false;
        settings.MixedContentMode = MixedContentHandling.AlwaysAllow;
        settings.UseWideViewPort = true;
        settings.LoadWithOverviewMode = true;

        _webView.SetWebViewClient(new WebViewClient());
        _webView.SetWebChromeClient(new WebChromeClient());

        SetContentView(_webView);
        _webView.LoadDataWithBaseURL("http://localhost/", GetAppHtml(), "text/html", "UTF-8", null);
    }

    public override void OnBackPressed()
    {
        if (_webView != null && _webView.CanGoBack())
            _webView.GoBack();
        else
            base.OnBackPressed();
    }

    private static string GetAppHtml()
    {
        return @"<!DOCTYPE html>
<html lang='ru'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no'>
<title>Sako Desk Mobile App</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; user-select: none; -webkit-user-select: none; }
  body { background: #0f111a; color: #eef2f7; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; overflow: hidden; height: 100vh; display: flex; flex-direction: column; }

  #topbar { height: 48px; background: #181b28; display: flex; align-items: center; justify-content: space-between; padding: 0 12px; border-bottom: 1px solid #2a2f45; z-index: 20; }
  .brand { font-weight: 800; color: #ff4757; font-size: 15px; display: flex; align-items: center; gap: 6px; }
  .stats { font-family: monospace; font-size: 12px; color: #2ed573; }

  #viewport { flex: 1; position: relative; background: #000; display: flex; align-items: center; justify-content: center; overflow: hidden; touch-action: none; }
  #screen { max-width: 100%; max-height: 100%; object-fit: contain; pointer-events: none; }
  #vcursor { position: absolute; width: 18px; height: 18px; border: 2px solid #ff4757; background: rgba(255,71,87,0.35); border-radius: 50%; transform: translate(-50%, -50%); pointer-events: none; display: none; z-index: 10; }

  #dock { background: #181b28; border-top: 1px solid #2a2f45; padding: 6px; display: flex; flex-direction: column; gap: 5px; z-index: 20; }
  .btn-row { display: flex; gap: 5px; justify-content: space-between; }
  button, select { flex: 1; background: #262b3d; color: #fff; border: 1px solid #38405a; border-radius: 7px; padding: 10px 4px; font-size: 12px; font-weight: 600; cursor: pointer; }
  button:active { background: #ff4757; border-color: #ff4757; }
  button.active-mode { background: #2ed573; color: #000; border-color: #2ed573; }

  #authModal { position: fixed; inset: 0; background: #0f111a; display: flex; align-items: center; justify-content: center; z-index: 100; padding: 18px; overflow-y: auto; }
  .card { background: #1c2030; border: 1px solid #2f3650; border-radius: 16px; padding: 24px; width: 100%; max-width: 380px; text-align: center; box-shadow: 0 12px 35px rgba(0,0,0,0.6); }
  .card h2 { color: #ff4757; margin-bottom: 4px; font-size: 24px; letter-spacing: 0.5px; }
  .card p { color: #9aa5be; font-size: 13px; margin-bottom: 18px; }
  .lbl { text-align: left; font-size: 12px; color: #a4b0be; margin-bottom: 6px; font-weight: 600; }
  input[type=password], input[type=text] { width: 100%; padding: 12px; border-radius: 9px; border: 1px solid #3b4362; background: #11131c; color: #fff; font-size: 16px; font-family: monospace; margin-bottom: 14px; outline: none; }
  input#passInput { color: #2ed573; font-weight: bold; text-align: center; font-size: 18px; }
  .btn-primary { width: 100%; background: #ff4757; border: none; padding: 14px; font-size: 16px; font-weight: bold; border-radius: 9px; color: #fff; margin-top: 4px; }
  #errMsg { color: #ff6b81; font-size: 13px; margin-top: 12px; min-height: 18px; }

  .drawer { display: none; gap: 5px; flex-direction: column; background: #131622; padding: 6px; border-radius: 8px; border: 1px solid #2b3148; }
  .drawer.show { display: flex; }
  .drawer-row { display: flex; gap: 5px; }
  #txtInput, #cmdInput { flex: 3; font-size: 14px; margin-bottom: 0; text-align: left; padding: 8px 10px; color: #fff; }
  #cmdOutput { max-height: 110px; overflow-y: auto; font-family: monospace; font-size: 11px; color: #7bed9f; background: #0b0d14; padding: 6px; border-radius: 6px; white-space: pre-wrap; user-select: text; }
</style>
</head>
<body>

<div id='authModal'>
  <div class='card'>
    <h2>⚡ SAKO DESK</h2>
    <p>Android-приложение удаленного управления ПК</p>

    <div class='lbl'>IP-адрес ПК (Wi-Fi) или 4G/5G ссылка (lhr.life):</div>
    <input type='text' id='hostInput' placeholder='192.168.1.10:9880 или https://xxx.lhr.life'>

    <div class='lbl'>Пароль доступа с ПК:</div>
    <input type='password' id='passInput' placeholder='Введите пароль' autocomplete='off'>

    <button class='btn-primary' onclick='doAuth()'>ПОДКЛЮЧИТЬСЯ К ПК ➜</button>
    <div id='errMsg'></div>
  </div>
</div>

<div id='topbar'>
  <div class='brand' id='pcNameLbl'>⚡ SAKO DESK</div>
  <div class='stats' id='statsLbl'>Ожидание...</div>
  <button style='flex:0;padding:6px 10px;' onclick='disconnect()'>✖ Выход</button>
</div>

<div id='viewport'>
  <img id='screen' alt='Удаленный экран'>
  <div id='vcursor'></div>
</div>

<div id='dock'>
  <div id='kbdDrawer' class='drawer'>
    <div class='drawer-row'>
      <input type='text' id='txtInput' placeholder='Текст для печати на ПК...'>
      <button onclick='sendText()'>Отпр.</button>
      <button onclick='sendVk(13)'>Enter</button>
      <button onclick='sendVk(8)'>⌫</button>
    </div>
  </div>

  <div id='sysDrawer' class='drawer'>
    <div class='drawer-row'>
      <button onclick='sendHotkey(""win_d"")'>🖥 Свернуть</button>
      <button onclick='sendHotkey(""alt_tab"")'>⇄ Alt+Tab</button>
      <button onclick='sendHotkey(""alt_f4"")'>✖ Alt+F4</button>
      <button onclick='sendHotkey(""taskmgr"")'>📊 Диспетчер</button>
    </div>
    <div class='drawer-row'>
      <button onclick='sendHotkey(""win"")'>🪟 Пуск</button>
      <button onclick='sendHotkey(""ctrl_c"")'>Копир.</button>
      <button onclick='sendHotkey(""ctrl_v"")'>Встав.</button>
      <button onclick='sendVk(27)'>Esc</button>
    </div>
  </div>

  <div id='termDrawer' class='drawer'>
    <div id='cmdOutput'>PowerShell терминал ПК готов...</div>
    <div class='drawer-row'>
      <input type='text' id='cmdInput' placeholder='Команда PowerShell / CMD...'>
      <button onclick='sendCmd()'>Выполнить</button>
    </div>
  </div>

  <div class='btn-row'>
    <button id='btnTouchMode' class='active-mode' onclick='toggleTouchMode()'>👆 Касание</button>
    <button onclick='triggerClick(""click"")'>ЛКМ</button>
    <button onclick='triggerClick(""dblclick"")'>2x Клик</button>
    <button onclick='triggerClick(""rclick"")'>ПКМ</button>
    <button id='btnDrag' onclick='toggleDrag()'>✊ Зажать</button>
  </div>
  <div class='btn-row'>
    <button onclick='toggleDrawer(""kbdDrawer"")'>⌨ Текст</button>
    <button onclick='toggleDrawer(""sysDrawer"")'>⚡ Система</button>
    <button onclick='toggleDrawer(""termDrawer"")'>💻 Терминал</button>
    <button onclick='sendScroll(240)'>⬆</button>
    <button onclick='sendScroll(-240)'>⬇</button>
    <select id='qualSel' onchange='changeQuality()'>
      <option value='85|1.0|30'>Ultra 30fps</option>
      <option value='60|0.8|24' selected>Быстрое</option>
      <option value='35|0.5|15'>Эконом 4G</option>
    </select>
  </div>
</div>

<script>
let ws = null;
let touchMode = 'direct';
let isDragging = false;
let curX = 0.5, curY = 0.5;
let lastTouchX = 0, lastTouchY = 0;
let frameCount = 0;
let lastFpsCheck = Date.now();
let currentBlobUrl = null;

const screenImg = document.getElementById('screen');
const viewport = document.getElementById('viewport');
const vcursor = document.getElementById('vcursor');

// Restore last used host
try {
  const savedHost = localStorage.getItem('sako_host');
  if (savedHost) document.getElementById('hostInput').value = savedHost;
} catch(e) {}

function buildWsUrl(rawHost) {
  let h = rawHost.trim();
  if (h.startsWith('https://')) {
    return 'wss://' + h.substring(8).replace(/\/+$/, '') + '/ws';
  }
  if (h.startsWith('http://')) {
    h = h.substring(7).replace(/\/+$/, '');
  }
  if (!h.includes(':') && !h.includes('.lhr.life') && !h.includes('.localhost.run')) {
    h = h + ':9880';
  }
  if (h.includes('.lhr.life') || h.includes('.localhost.run')) {
    return 'wss://' + h.replace(/\/+$/, '') + '/ws';
  }
  return 'ws://' + h + '/ws';
}

function doAuth() {
  const host = document.getElementById('hostInput').value.trim();
  const pass = document.getElementById('passInput').value.trim();
  if (!host) { document.getElementById('errMsg').innerText = 'Укажите IP-адрес ПК или ссылку!'; return; }
  if (!pass) { document.getElementById('errMsg').innerText = 'Введите пароль с ПК!'; return; }

  try { localStorage.setItem('sako_host', host); } catch(e) {}

  const wsUrl = buildWsUrl(host);
  document.getElementById('errMsg').innerText = 'Подключение к ' + wsUrl + '...';

  if (ws) { try { ws.close(); } catch(e) {} }
  ws = new WebSocket(wsUrl);
  ws.binaryType = 'arraybuffer';

  ws.onopen = () => {
    ws.send(JSON.stringify({ type: 'auth', password: pass }));
  };

  ws.onerror = () => {
    document.getElementById('errMsg').innerText = 'Не удалось соединиться с ПК. Проверьте адрес.';
  };

  ws.onmessage = (evt) => {
    if (typeof evt.data === 'string') {
      const msg = JSON.parse(evt.data);
      if (msg.type === 'auth_ok') {
        document.getElementById('authModal').style.display = 'none';
        document.getElementById('pcNameLbl').innerText = '⚡ ' + (msg.machine || 'ПК');
      } else if (msg.type === 'auth_fail') {
        document.getElementById('errMsg').innerText = msg.msg;
      } else if (msg.type === 'cmd_out') {
        const out = document.getElementById('cmdOutput');
        out.innerText = msg.output || '(Выполнено без вывода)';
        out.scrollTop = out.scrollHeight;
      }
    } else {
      const blob = new Blob([evt.data], { type: 'image/jpeg' });
      const url = URL.createObjectURL(blob);
      screenImg.onload = () => {
        if (currentBlobUrl) URL.revokeObjectURL(currentBlobUrl);
        currentBlobUrl = url;
      };
      screenImg.src = url;

      frameCount++;
      const now = Date.now();
      if (now - lastFpsCheck >= 1000) {
        document.getElementById('statsLbl').innerText = '🟢 ' + frameCount + ' FPS';
        frameCount = 0;
        lastFpsCheck = now;
      }
    }
  };

  ws.onclose = () => {
    document.getElementById('statsLbl').innerText = '🔴 Отключено';
  };
}

function disconnect() {
  if (ws) { try { ws.close(); } catch(e) {} }
  document.getElementById('authModal').style.display = 'flex';
  document.getElementById('errMsg').innerText = 'Сессия завершена.';
}

function mapClientToNorm(clientX, clientY) {
  const rect = screenImg.getBoundingClientRect();
  if (rect.width <= 0 || rect.height <= 0) return { x: 0.5, y: 0.5 };
  const nx = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
  const ny = Math.max(0, Math.min(1, (clientY - rect.top) / rect.height));
  return { x: nx, y: ny };
}

function updateVirtualCursor() {
  const rect = screenImg.getBoundingClientRect();
  const vpRect = viewport.getBoundingClientRect();
  const px = (rect.left - vpRect.left) + curX * rect.width;
  const py = (rect.top - vpRect.top) + curY * rect.height;
  vcursor.style.left = px + 'px';
  vcursor.style.top = py + 'px';
}

function sendMouse(action, x, y, delta = 0) {
  if (!ws || ws.readyState !== 1) return;
  ws.send(JSON.stringify({ type: 'mouse', action, x, y, delta }));
}

let tapStartTime = 0;
let tapMoved = false;

viewport.addEventListener('touchstart', (e) => {
  if (e.touches.length === 1) {
    const t = e.touches[0];
    lastTouchX = t.clientX;
    lastTouchY = t.clientY;
    tapStartTime = Date.now();
    tapMoved = false;

    if (touchMode === 'direct') {
      const norm = mapClientToNorm(t.clientX, t.clientY);
      curX = norm.x; curY = norm.y;
      sendMouse('move', curX, curY);
    }
  }
}, { passive: false });

viewport.addEventListener('touchmove', (e) => {
  e.preventDefault();
  if (e.touches.length === 1) {
    const t = e.touches[0];
    const dx = t.clientX - lastTouchX;
    const dy = t.clientY - lastTouchY;
    if (Math.abs(dx) > 4 || Math.abs(dy) > 4) tapMoved = true;

    if (touchMode === 'direct') {
      const norm = mapClientToNorm(t.clientX, t.clientY);
      curX = norm.x; curY = norm.y;
    } else {
      curX = Math.max(0, Math.min(1, curX + dx / 350));
      curY = Math.max(0, Math.min(1, curY + dy / 350));
      updateVirtualCursor();
    }
    lastTouchX = t.clientX;
    lastTouchY = t.clientY;
    sendMouse('move', curX, curY);
  }
}, { passive: false });

viewport.addEventListener('touchend', (e) => {
  const dt = Date.now() - tapStartTime;
  if (!tapMoved && dt < 280) {
    sendMouse('click', curX, curY);
  } else if (!tapMoved && dt >= 500) {
    sendMouse('rclick', curX, curY);
  }
});

function toggleTouchMode() {
  const btn = document.getElementById('btnTouchMode');
  if (touchMode === 'direct') {
    touchMode = 'trackpad';
    btn.innerText = '🖱 Тачпад';
    vcursor.style.display = 'block';
    updateVirtualCursor();
  } else {
    touchMode = 'direct';
    btn.innerText = '👆 Касание';
    vcursor.style.display = 'none';
  }
}

function toggleDrag() {
  isDragging = !isDragging;
  const btn = document.getElementById('btnDrag');
  btn.classList.toggle('active-mode', isDragging);
  sendMouse(isDragging ? 'down' : 'up', curX, curY);
}

function triggerClick(act) { sendMouse(act, curX, curY); }
function sendScroll(delta) { sendMouse('wheel', curX, curY, delta); }

function toggleDrawer(id) {
  ['kbdDrawer', 'sysDrawer', 'termDrawer'].forEach(d => {
    if (d === id) document.getElementById(d).classList.toggle('show');
    else document.getElementById(d).classList.remove('show');
  });
}

function sendText() {
  const inp = document.getElementById('txtInput');
  if (inp.value && ws && ws.readyState === 1) {
    ws.send(JSON.stringify({ type: 'text', text: inp.value }));
    inp.value = '';
  }
}

function sendVk(vk) {
  if (ws && ws.readyState === 1) ws.send(JSON.stringify({ type: 'key', vk: vk }));
}

function sendHotkey(combo) {
  if (ws && ws.readyState === 1) ws.send(JSON.stringify({ type: 'hotkey', combo: combo }));
}

function sendCmd() {
  const inp = document.getElementById('cmdInput');
  if (inp.value && ws && ws.readyState === 1) {
    document.getElementById('cmdOutput').innerText = 'Выполнение: ' + inp.value + '...';
    ws.send(JSON.stringify({ type: 'cmd', command: inp.value }));
    inp.value = '';
  }
}

function changeQuality() {
  const val = document.getElementById('qualSel').value.split('|');
  if (ws && ws.readyState === 1) {
    ws.send(JSON.stringify({ type: 'quality', q: parseInt(val[0]), s: parseFloat(val[1]), fps: parseInt(val[2]) }));
  }
}
</script>
</body>
</html>";
    }
}
