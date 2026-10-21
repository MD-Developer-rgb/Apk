import json
import socket
import threading
import time

from kivy.app import App
from kivy.clock import Clock
from kivy.lang import Builder
from kivy.properties import StringProperty
from kivy.uix.boxlayout import BoxLayout
from kivy.utils import platform

DISCOVERY_PORT = 37020
DISCOVERY_PACKET = b"LAN_CHAT_DISCOVER"
DEFAULT_TCP_PORT = 5050

KV = r"""
<ChatRoot>:
    orientation: "vertical"
    padding: "12dp"
    spacing: "8dp"
    Label:
        text: "LAN Chat"
        size_hint_y: None
        height: "42dp"
        font_size: "24sp"
        bold: True
    Label:
        text: root.status
        size_hint_y: None
        height: "42dp"
        text_size: self.width, None
        halign: "left"
        valign: "middle"
    BoxLayout:
        size_hint_y: None
        height: "44dp"
        spacing: "6dp"
        TextInput:
            id: ip_input
            hint_text: "Server LAN IP (auto-discovered)"
            text: root.server_ip
            multiline: False
        TextInput:
            id: port_input
            text: "5050"
            input_filter: "int"
            size_hint_x: .28
            multiline: False
    BoxLayout:
        size_hint_y: None
        height: "44dp"
        spacing: "6dp"
        Button:
            text: "Find server"
            on_release: root.discover()
        Button:
            text: "Connect"
            on_release: root.connect_to(ip_input.text, port_input.text)
        Button:
            text: "Disconnect"
            on_release: root.disconnect()
    Label:
        text: "Messages"
        size_hint_y: None
        height: "24dp"
        halign: "left"
        text_size: self.size
    TextInput:
        id: chat_log
        readonly: True
        multiline: True
        size_hint_y: 1
        text: root.chat_text
        cursor_blink: False
    BoxLayout:
        size_hint_y: None
        height: "50dp"
        spacing: "6dp"
        TextInput:
            id: message_input
            hint_text: "Type a message..."
            multiline: False
            on_text_validate: root.send_message(message_input.text)
        Button:
            text: "Send"
            size_hint_x: .25
            on_release: root.send_message(message_input.text)
"""


class ChatRoot(BoxLayout):
    status = StringProperty("Searching for a Python server on this LAN...")
    server_ip = StringProperty("")
    chat_text = StringProperty("")

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.sock = None
        self.connected = False
        self.send_lock = threading.Lock()
        Clock.schedule_once(lambda _dt: self.discover(), 0.8)

    def append_chat(self, text):
        def _append(_dt):
            self.chat_text += text + "\n"
            self.ids.chat_log.cursor = (0, len(self.chat_text.splitlines()))
        Clock.schedule_once(_append, 0)

    def discover(self):
        self.status = "Looking for server..."
        threading.Thread(target=self._discover_worker, daemon=True).start()

    def _discover_worker(self):
        found = None
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            s.settimeout(1.5)
            # Broadcast on local subnet; try both general and directed broadcast.
            targets = [("255.255.255.255", DISCOVERY_PORT)]
            try:
                # Infer local subnet from the route used for a UDP connection.
                probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                probe.connect(("8.8.8.8", 80))
                local_ip = probe.getsockname()[0]
                probe.close()
                parts = local_ip.split(".")
                if len(parts) == 4:
                    targets.append((f"{parts[0]}.{parts[1]}.{parts[2]}.255", DISCOVERY_PORT))
            except OSError:
                pass
            for target in targets:
                try:
                    s.sendto(DISCOVERY_PACKET, target)
                except OSError:
                    pass
            deadline = time.time() + 2.5
            while time.time() < deadline:
                try:
                    data, _addr = s.recvfrom(2048)
                    payload = json.loads(data.decode("utf-8"))
                    if payload.get("type") == "LAN_CHAT_SERVER":
                        found = payload
                        break
                except socket.timeout:
                    break
                except (ValueError, OSError):
                    continue
        except OSError as exc:
            Clock.schedule_once(lambda _dt: setattr(self, "status", f"Discovery error: {exc}"), 0)
        finally:
            s.close()
        if found:
            def _found(_dt):
                self.server_ip = found.get("ip", "")
                self.ids.ip_input.text = self.server_ip
                self.ids.port_input.text = str(found.get("port", DEFAULT_TCP_PORT))
                self.status = f"Found {found.get('name', 'server')} at {self.server_ip}. Tap Connect."
            Clock.schedule_once(_found, 0)
        else:
            Clock.schedule_once(lambda _dt: setattr(
                self, "status",
                "No server found. Start server.py on the same Wi-Fi, then tap Find server or enter its LAN IP."
            ), 0)

    def connect_to(self, ip, port):
        ip = (ip or "").strip()
        try:
            port = int(port)
            if not ip or not (1 <= port <= 65535):
                raise ValueError("Enter a valid IP and port.")
        except ValueError:
            self.status = "Enter a valid server IP and port."
            return
        self.disconnect()
        self.status = f"Connecting to {ip}:{port}..."
        threading.Thread(target=self._connect_worker, args=(ip, port), daemon=True).start()

    def _connect_worker(self, ip, port):
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(5)
        try:
            s.connect((ip, port))
            s.settimeout(None)
            self.sock = s
            self.connected = True
            Clock.schedule_once(lambda _dt: setattr(self, "status", f"Connected to {ip}:{port}"), 0)
            self.append_chat(f"--- Connected to {ip}:{port} ---")
            threading.Thread(target=self._receive_loop, args=(s,), daemon=True).start()
        except OSError as exc:
            s.close()
            Clock.schedule_once(lambda _dt: setattr(self, "status", f"Connection failed: {exc}"), 0)

    def _receive_loop(self, sock):
        buffer = ""
        try:
            while True:
                chunk = sock.recv(4096)
                if not chunk:
                    break
                buffer += chunk.decode("utf-8", errors="replace")
                while "\n" in buffer:
                    line, buffer = buffer.split("\n", 1)
                    if not line.strip():
                        continue
                    try:
                        msg = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    kind = msg.get("type")
                    if kind == "message":
                        self.append_chat(f"[{msg.get('time', '')}] {msg.get('from', 'Server')}: {msg.get('text', '')}")
                    elif kind == "system":
                        self.append_chat(f"--- {msg.get('text', '')} ---")
        except OSError:
            pass
        finally:
            if self.sock is sock:
                self.sock = None
                self.connected = False
                Clock.schedule_once(lambda _dt: setattr(self, "status", "Disconnected."), 0)
                self.append_chat("--- Connection closed ---")
            try:
                sock.close()
            except OSError:
                pass

    def send_message(self, text):
        text = (text or "").strip()
        if not text:
            return
        sock = self.sock
        if not self.connected or sock is None:
            self.status = "Connect to the server before sending messages."
            return
        payload = (json.dumps({"type": "message", "text": text}, ensure_ascii=False) + "\n").encode("utf-8")
        try:
            with self.send_lock:
                sock.sendall(payload)
            self.ids.message_input.text = ""
        except OSError as exc:
            self.status = f"Send failed: {exc}"

    def disconnect(self):
        sock = self.sock
        self.sock = None
        self.connected = False
        if sock:
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                sock.close()
            except OSError:
                pass


class LANChatApp(App):
    def build(self):
        self.title = "LAN Chat"
        Builder.load_string(KV)
        return ChatRoot()


if __name__ == "__main__":
    LANChatApp().run()
