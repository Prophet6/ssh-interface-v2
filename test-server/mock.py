"""
Bidirectional mock for SSH Interface v2.

  ssh-server  This PC listens (Crestron Client module connects here)
  tcp-client  This PC dials out (SIMPL Windows TCP/IP Server listens)

Type a line and press Enter to send. Incoming data prints as [RX].
Ctrl+C stops.
"""

from __future__ import print_function

import argparse
import hashlib
import logging
import os
import socket
import sys
import threading
import time

import paramiko
from paramiko import AUTH_FAILED, AUTH_SUCCESSFUL, OPEN_SUCCEEDED
from paramiko.common import OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
from paramiko.server import InteractiveQuery

HERE = os.path.dirname(os.path.abspath(__file__))
HOST_KEY_PATH = os.path.join(HERE, "host_key")
STOP = threading.Event()
ACTIVE_SEND = [None]
ACTIVE_EOL = ["cr"]


def local_ipv4():
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.connect(("8.8.8.8", 80))
        ip = sock.getsockname()[0]
        sock.close()
        return ip
    except Exception:
        return "127.0.0.1"


def md5_fingerprint(key):
    digest = hashlib.md5(key.asbytes()).hexdigest().upper()
    return ":".join(digest[i:i + 2] for i in range(0, len(digest), 2))


def load_or_make_host_key(rotate):
    if rotate and os.path.isfile(HOST_KEY_PATH):
        os.remove(HOST_KEY_PATH)
        print("Deleted host_key (new fingerprint on next start).")
        return None

    if os.path.isfile(HOST_KEY_PATH):
        return paramiko.RSAKey.from_private_key_file(HOST_KEY_PATH)

    key = paramiko.RSAKey.generate(2048)
    key.write_private_key_file(HOST_KEY_PATH)
    print("Wrote new host_key:", HOST_KEY_PATH)
    return key


def encode_eol(text, eol):
    if text.endswith("\r\n"):
        text = text[:-2]
    elif text.endswith("\n") or text.endswith("\r"):
        text = text[:-1]

    if eol == "cr":
        return text + "\r"
    if eol == "lf":
        return text + "\n"
    if eol == "crlf":
        return text + "\r\n"
    return text


def preview(data):
    return data.replace("\r", "\\r").replace("\n", "\\n")


def stdin_worker():
    while not STOP.is_set():
        try:
            line = sys.stdin.readline()
        except Exception:
            STOP.set()
            return
        if line == "":
            return
        fn = ACTIVE_SEND[0]
        if fn is None:
            print("(no session — line ignored)")
            continue
        payload = encode_eol(line, ACTIVE_EOL[0])
        try:
            fn(payload.encode("utf-8"))
        except Exception as exc:
            print("[send failed]", exc)
            continue
        sys.stdout.write("[TX] {0}\n".format(preview(payload)))
        sys.stdout.flush()


def start_stdin(eol):
    ACTIVE_EOL[0] = eol
    thread = threading.Thread(target=stdin_worker, name="stdin")
    thread.daemon = True
    thread.start()


def rx_loop(recv_fn, send_fn, echo, session_done):
    while not STOP.is_set() and not session_done.is_set():
        try:
            chunk = recv_fn()
        except Exception as exc:
            if not STOP.is_set():
                print("\n[peer closed] {0}".format(exc))
            session_done.set()
            return
        if chunk is None:
            time.sleep(0.05)
            continue
        if chunk == b"":
            print("\n[peer closed]")
            session_done.set()
            return
        try:
            text = chunk.decode("utf-8", "replace")
        except Exception:
            text = repr(chunk)
        sys.stdout.write("[RX] {0}\n".format(preview(text)))
        sys.stdout.flush()
        if echo:
            try:
                send_fn(chunk)
                sys.stdout.write("[TX echo] {0}\n".format(preview(text)))
                sys.stdout.flush()
            except Exception as exc:
                print("[echo failed]", exc)
                session_done.set()
                return


def run_session(recv_fn, send_fn, echo):
    session_done = threading.Event()
    ACTIVE_SEND[0] = send_fn
    rx = threading.Thread(target=rx_loop, args=(recv_fn, send_fn, echo, session_done))
    rx.daemon = True
    rx.start()
    print("Type to send. Ctrl+C to stop. echo={0}".format(echo))
    try:
        while not STOP.is_set() and not session_done.is_set():
            time.sleep(0.1)
    except KeyboardInterrupt:
        print("\nStopping.")
        STOP.set()
    ACTIVE_SEND[0] = None
    session_done.set()


def pump_client(recv_fn, send_fn, echo, eol):
    """Client modes: stdin is bound to this one session."""
    session_done = threading.Event()

    def reader():
        rx_loop(recv_fn, send_fn, echo, session_done)

    thread = threading.Thread(target=reader, name="rx")
    thread.daemon = True
    thread.start()
    print("Type to send. Ctrl+C to stop. eol={0} echo={1}".format(eol, echo))
    try:
        while not STOP.is_set() and not session_done.is_set():
            try:
                line = sys.stdin.readline()
            except Exception:
                break
            if line == "":
                if sys.stdin.isatty():
                    STOP.set()
                    break
                time.sleep(0.8)
                session_done.set()
                break
            payload = encode_eol(line, eol)
            try:
                send_fn(payload.encode("utf-8"))
            except Exception as exc:
                print("[send failed]", exc)
                session_done.set()
                break
            sys.stdout.write("[TX] {0}\n".format(preview(payload)))
            sys.stdout.flush()
    except KeyboardInterrupt:
        print("\nStopping.")
        STOP.set()
    session_done.set()


class MockSshServer(paramiko.ServerInterface):
    def __init__(self, username, password):
        self.username = username
        self.password = password
        self.event = threading.Event()
        self._interactive_user = None

    def check_channel_request(self, kind, chanid):
        if kind == "session":
            return OPEN_SUCCEEDED
        return OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def get_allowed_auths(self, username):
        return "password,keyboard-interactive"

    def check_auth_password(self, username, password):
        if username == self.username and password == self.password:
            return AUTH_SUCCESSFUL
        return AUTH_FAILED

    def check_auth_interactive(self, username, submethods):
        self._interactive_user = username
        query = InteractiveQuery("", "SSH mock login")
        query.add_prompt("Password: ", False)
        return query

    def check_auth_interactive_response(self, responses):
        password = responses[0] if responses else ""
        if self._interactive_user == self.username and password == self.password:
            return AUTH_SUCCESSFUL
        return AUTH_FAILED

    def check_channel_pty_request(self, channel, term, width, height, pixelwidth, pixelheight, modes):
        return True

    def check_channel_shell_request(self, channel):
        self.event.set()
        return True

    def check_channel_exec_request(self, channel, command):
        return False


def run_ssh_server(args):
    key = load_or_make_host_key(False)
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind((args.bind, args.port))
    sock.listen(4)
    sock.settimeout(1.0)

    print("SSH server listening on {0}:{1}".format(args.bind, args.port))
    print("This PC LAN IP : {0}".format(local_ipv4()))
    print("Username       : {0}".format(args.user))
    print("Password       : {0}".format(args.password))
    print("Host key MD5   : {0}".format(md5_fingerprint(key)))
    print("Crestron Client: IP_Address$={0}  IP_Port={1}  user/pass as above".format(local_ipv4(), args.port))
    start_stdin(args.eol)

    while not STOP.is_set():
        print("Waiting for SSH connection...")
        client = None
        while not STOP.is_set():
            try:
                client, addr = sock.accept()
                break
            except socket.timeout:
                continue
            except KeyboardInterrupt:
                STOP.set()
                sock.close()
                return

        if client is None:
            break

        print("TCP accept from {0}:{1}".format(addr[0], addr[1]))
        transport = paramiko.Transport(client)
        transport.banner_timeout = 8
        transport.add_server_key(key)
        server = MockSshServer(args.user, args.password)
        try:
            transport.start_server(server=server)
        except paramiko.SSHException as exc:
            print("Ignored non-SSH TCP from {0}:{1} ({2})".format(addr[0], addr[1], exc))
            try:
                transport.close()
            except Exception:
                pass
            continue

        channel = transport.accept(30)
        if channel is None:
            print("No channel (auth failed or timed out).")
            transport.close()
            continue

        server.event.wait(10)
        banner = "SSH mock ready. Type on either side; this console is bidirectional.\r\n"
        try:
            channel.send(banner)
        except Exception:
            transport.close()
            continue
        print("SSH session up. {0}".format(preview(banner)))

        def recv():
            if channel.closed:
                return b""
            if channel.recv_ready():
                return channel.recv(4096)
            return None

        def send(data):
            channel.sendall(data)

        try:
            run_session(recv, send, args.echo)
        finally:
            try:
                channel.close()
            except Exception:
                pass
            try:
                transport.close()
            except Exception:
                pass

    sock.close()


def run_tcp_client(args):
    print("TCP client connecting to {0}:{1} ...".format(args.host, args.port))
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(10)
    sock.connect((args.host, args.port))
    sock.settimeout(0.2)
    print("TCP connected.")

    def recv():
        try:
            return sock.recv(4096)
        except socket.timeout:
            return None

    def send(data):
        sock.sendall(data)

    try:
        pump_client(recv, send, args.echo, args.eol)
    finally:
        STOP.set()
        sock.close()


def build_parser():
    parser = argparse.ArgumentParser(description="SSH Interface v2 mock")
    parser.add_argument(
        "mode",
        choices=["ssh-server", "tcp-client"],
        help="ssh-server: Crestron Client test. tcp-client: built-in TCP/IP Server test.",
    )
    parser.add_argument("--host", default="127.0.0.1", help="Remote host (client modes)")
    parser.add_argument("--port", type=int, default=0, help="Port (defaults: ssh 2222, tcp 5000)")
    parser.add_argument("--bind", default="0.0.0.0", help="Listen address (server modes)")
    parser.add_argument("--user", default="crestron", help="SSH username")
    parser.add_argument("--password", default="crestron", help="SSH password")
    parser.add_argument("--eol", choices=["cr", "lf", "crlf", "none"], default="cr",
                        help="Line ending appended when you press Enter (default cr)")
    parser.add_argument("--echo", action="store_true", help="Loop received bytes back to the peer")
    parser.add_argument("--rotate-key", action="store_true",
                        help="Delete host_key and exit (next ssh-server start gets a new fingerprint)")
    return parser


def main():
    try:
        sys.stdout.reconfigure(line_buffering=True)
        sys.stderr.reconfigure(line_buffering=True)
    except Exception:
        pass
    logging.getLogger("paramiko").setLevel(logging.CRITICAL)
    args = build_parser().parse_args()
    if args.rotate_key:
        load_or_make_host_key(True)
        return

    if args.port == 0:
        if args.mode.startswith("ssh"):
            args.port = 2222
        else:
            args.port = 5000

    try:
        if args.mode == "ssh-server":
            run_ssh_server(args)
        elif args.mode == "tcp-client":
            run_tcp_client(args)
    except KeyboardInterrupt:
        print("\nStopping.")
        STOP.set()


if __name__ == "__main__":
    main()
