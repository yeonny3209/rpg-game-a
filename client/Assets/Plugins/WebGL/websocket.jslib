// Minimal WebSocket bridge for Unity WebGL.
// Polling design (C# pulls queued messages each frame) → no callbacks into C#, no MonoPInvokeCallback issues.
// Also exposes the HTML login/message overlay helpers from the RpgTemplate WebGL template.
mergeInto(LibraryManager.library, {

  WsCreate: function (urlPtr) {
    var url = UTF8ToString(urlPtr);
    if (!window.RpgWs) window.RpgWs = { sockets: {}, next: 1 };
    var id = window.RpgWs.next++;
    var s = { ws: null, queue: [], state: 0 }; // 0 connecting, 1 open, 2 closing, 3 closed
    try {
      var ws = new WebSocket(url);
      ws.binaryType = 'arraybuffer';
      ws.onopen = function () { s.state = 1; };
      ws.onclose = function () { s.state = 3; };
      ws.onerror = function () { s.state = 3; };
      ws.onmessage = function (e) {
        if (e.data instanceof ArrayBuffer) s.queue.push(new Uint8Array(e.data));
      };
      s.ws = ws;
    } catch (err) {
      console.error('WebSocket create failed', err);
      s.state = 3;
    }
    window.RpgWs.sockets[id] = s;
    return id;
  },

  WsState: function (id) {
    var s = window.RpgWs && window.RpgWs.sockets[id];
    return s ? s.state : 3;
  },

  WsSend: function (id, ptr, len) {
    var s = window.RpgWs && window.RpgWs.sockets[id];
    if (!s || s.state !== 1) return 0;
    // slice() copies out of the (possibly growing) wasm heap before sending.
    s.ws.send(HEAPU8.slice(ptr, ptr + len));
    return 1;
  },

  WsPeekSize: function (id) {
    var s = window.RpgWs && window.RpgWs.sockets[id];
    if (!s || s.queue.length === 0) return -1;
    return s.queue[0].length;
  },

  WsReceive: function (id, ptr, maxLen) {
    var s = window.RpgWs && window.RpgWs.sockets[id];
    if (!s || s.queue.length === 0) return -1;
    var msg = s.queue[0];
    if (msg.length > maxLen) return -2;
    s.queue.shift();
    HEAPU8.set(msg, ptr);
    return msg.length;
  },

  WsClose: function (id) {
    var s = window.RpgWs && window.RpgWs.sockets[id];
    if (!s) return;
    try { if (s.ws) s.ws.close(); } catch (e) { }
    s.state = 3;
    delete window.RpgWs.sockets[id];
  },

  RpgGetNickname: function () {
    var str = window.rpgNickname || '';
    var size = lengthBytesUTF8(str) + 1;
    var buf = _malloc(size);
    stringToUTF8(str, buf, size);
    return buf;
  },

  RpgShowMessage: function (textPtr, kind) {
    var text = UTF8ToString(textPtr);
    if (window.rpgShowMessage) window.rpgShowMessage(text, kind);
    else console.log(text);
  },

  RpgShowLogin: function (show) {
    if (window.rpgShowLogin) window.rpgShowLogin(!!show);
  },

  // Chat typing happens in an HTML input (Unity WebGL cannot take Korean IME text).
  RpgChatOpen: function (channel, prefillPtr) {
    if (window.rpgChatOpen) window.rpgChatOpen(channel, UTF8ToString(prefillPtr));
  },

  RpgChatClose: function () {
    if (window.rpgChatClose) window.rpgChatClose();
  }
});
