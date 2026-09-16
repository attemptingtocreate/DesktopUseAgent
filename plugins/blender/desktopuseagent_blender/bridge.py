import json
import queue
import threading
import time
import urllib.parse
import urllib.request
import uuid

import bpy

from . import config
from . import operations

SESSION_ID = uuid.uuid4().hex
COMMAND_QUEUE_MAX = 8
RESULT_QUEUE_MAX = 8

_polling_enabled = False
_timer_registered = False
_backoff = 1.0
_stop_event = threading.Event()
_network_thread = None
_command_queue = queue.Queue(maxsize=COMMAND_QUEUE_MAX)
_result_queue = queue.Queue(maxsize=RESULT_QUEUE_MAX)
_state_lock = threading.Lock()
_session_state = {"blenderVersion": "", "fileName": ""}


def is_polling_enabled():
    return _polling_enabled


def set_polling_enabled(enabled: bool):
    global _polling_enabled
    enabled = bool(enabled)
    if enabled == _polling_enabled:
        return
    _polling_enabled = enabled
    if enabled:
        _stop_event.clear()
        _ensure_network_thread()
        ensure_timer()
    else:
        _stop_event.set()
        _join_network_thread()
        _drain_queue(_command_queue)
        _drain_queue(_result_queue)


def register():
    ensure_timer()


def unregister():
    set_polling_enabled(False)
    global _timer_registered
    _timer_registered = False


def ensure_timer():
    global _timer_registered
    if not _timer_registered:
        bpy.app.timers.register(_drain_main_thread, first_interval=0.1, persistent=True)
        _timer_registered = True


def _drain_queue(q):
    while True:
        try:
            q.get_nowait()
        except queue.Empty:
            break


def _ensure_network_thread():
    global _network_thread
    if _network_thread is not None and _network_thread.is_alive():
        return
    _stop_event.clear()
    _network_thread = threading.Thread(
        target=_network_worker,
        name="DesktopUseAgentBlenderBridge",
        daemon=True,
    )
    _network_thread.start()


def _join_network_thread():
    global _network_thread
    thread = _network_thread
    _network_thread = None
    if thread is not None and thread.is_alive():
        thread.join(timeout=2.0)


def _update_session_state():
    try:
        with _state_lock:
            _session_state["blenderVersion"] = bpy.app.version_string
            _session_state["fileName"] = bpy.data.filepath or ""
    except Exception:
        pass


def _base_url(path):
    token = urllib.parse.quote(config.TOKEN or "")
    return f"http://{config.HOST}:{config.PORT}{path}?token={token}"


def _post(path, body):
    data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(
        _base_url(path),
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=10) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _get(path, query=""):
    url = _base_url(path)
    if query:
        url += "&" + query
    with urllib.request.urlopen(url, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _post_pending_results():
    while True:
        try:
            payload = _result_queue.get_nowait()
        except queue.Empty:
            break
        try:
            _post("/v1/result", payload)
        except Exception:
            pass


def _network_worker():
    global _backoff
    while not _stop_event.is_set():
        _post_pending_results()
        if not _polling_enabled or not config.TOKEN:
            time.sleep(min(_backoff, 8.0))
            continue

        try:
            with _state_lock:
                version = _session_state["blenderVersion"]
                file_name = _session_state["fileName"]
            _post(
                "/v1/register",
                {
                    "sessionId": SESSION_ID,
                    "blenderVersion": version,
                    "fileName": file_name,
                    "pollingEnabled": True,
                },
            )
            _post("/v1/heartbeat", {"sessionId": SESSION_ID, "fileName": file_name})
            payload = _get("/v1/poll", f"sessionId={urllib.parse.quote(SESSION_ID)}")
            command = payload.get("command")
            if command:
                try:
                    _command_queue.put_nowait(command)
                except queue.Full:
                    pass
            _backoff = 1.0
        except Exception:
            _backoff = min(_backoff * 1.5, 8.0)
            time.sleep(_backoff)


def _drain_main_thread():
    _update_session_state()
    if not _polling_enabled:
        return 0.5

    try:
        command = _command_queue.get_nowait()
    except queue.Empty:
        return 0.1

    command_id = command.get("id")
    operation = command.get("operation")
    params = command.get("params") or {}
    try:
        data = operations.execute(operation, params)
        payload = {"sessionId": SESSION_ID, "commandId": command_id, "ok": True, "data": data}
    except Exception as ex:
        payload = {
            "sessionId": SESSION_ID,
            "commandId": command_id,
            "ok": False,
            "error": str(ex)[:500],
        }

    try:
        _result_queue.put_nowait(payload)
    except queue.Full:
        pass

    return 0.0
