# -*- coding: utf-8 -*-
import csv
import http.client
import json
import math
import os
import random
import re
import struct
import sys
import ctypes
import shutil
import socket
import subprocess
import threading
import tempfile
import time
import urllib.parse
import webbrowser
import platform
from datetime import datetime
from copy import deepcopy

import tkinter as tk
from tkinter import filedialog, messagebox, scrolledtext, ttk


def no_window_kwargs():
    """Opcoes para um subprocesso nao abrir janela de consola.

    A app corre sem consola (pythonw / PyInstaller --noconsole). Sem estas
    flags, cada `reg`, `sc`, `winget`, `bcdedit` ou `powershell` invocado
    pisca um prompt preto no ecra, um por chamada.
    """
    if os.name != "nt":
        return {}
    startupinfo = subprocess.STARTUPINFO()
    startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startupinfo.wShowWindow = subprocess.SW_HIDE
    return {
        "creationflags": getattr(subprocess, "CREATE_NO_WINDOW", 0),
        "startupinfo": startupinfo,
    }


def open_uri(uri):
    """Abre um URI (ms-settings:, https:) pela shell do Windows, sem cmd.exe."""
    try:
        os.startfile(uri)
        return True
    except OSError:
        return bool(webbrowser.open(uri))


try:
    import psutil
except ImportError:
    try:
        subprocess.run(
            [sys.executable, "-m", "pip", "install", "psutil"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=180,
            check=True,
            **no_window_kwargs(),
        )
        import psutil
    except Exception as exc:
        raise SystemExit(
            f"psutil em falta e a instalacao automatica falhou ({exc}).\n"
            f"Instala com: {sys.executable} -m pip install psutil"
        ) from exc


def check_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def relaunch_as_admin():
    """Reabre a app com elevacao de administrador.

    Chamado apenas pelo bloco __main__, para que o módulo possa ser
    importado (em testes) sem pedir elevacao. No executavel congelado
    (PyInstaller) o proprio .exe e reaberto, sem argumentos.
    """
    params = "" if getattr(sys, "frozen", False) else f'"{__file__}"'
    try:
        result = ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, params, None, 1)
    except Exception:
        return False
    return bool(result and result > 32)


def emit(text):
    """Escreve em stdout quando existe console (build --windowed nao tem)."""
    stream = sys.stdout
    if stream is None:
        return
    try:
        stream.write(text + "\n")
        stream.flush()
    except Exception:
        pass


def run_selfcheck():
    """Valida o runtime sem abrir a interface (usado pelo host e por testes)."""
    problems = []
    if sys.version_info < (3, 8):
        problems.append(f"python {sys.version.split()[0]} < 3.8")
    try:
        import psutil  # noqa: F401
    except Exception as exc:
        problems.append(f"psutil indisponivel ({exc})")
    try:
        import tkinter  # noqa: F401
    except Exception as exc:
        problems.append(f"tkinter indisponivel ({exc})")
    return problems


APP_DIR = os.path.dirname(os.path.abspath(__file__))


def resolve_data_dir():
    """Pasta de dados: Launcher > executavel congelado > pasta do script."""
    configured = os.environ.get("RIBANENSE_APP_DATA")
    if configured:
        return configured
    if getattr(sys, "frozen", False):
        base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~")
        return os.path.join(base, "MagoDaPerformance")
    return APP_DIR


DATA_DIR = resolve_data_dir()
os.makedirs(DATA_DIR, exist_ok=True)
LOG_FILE = os.path.join(DATA_DIR, "MagoDaPerformance_log.txt")
BACKUP_FILE = os.path.join(DATA_DIR, "mago_backup.json")
SNAPSHOT_DIR = os.path.join(DATA_DIR, "mago_snapshots")

TRACKED_REGS = [
    (r"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode"),
    (r"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrLevel"),
    (r"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak", "DisplayPowerSaving"),
    (r"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation"),
    (r"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled"),
    (r"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode"),
    (r"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity"),
    (r"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled"),
    (r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness"),
    (r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex"),
    (r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority"),
    (r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority"),
    (r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category"),
    (r"HKCU\System\GameConfigStore", "GameDVR_Enabled"),
    (r"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
    (r"HKCU\Control Panel\Mouse", "MouseSpeed"),
    (r"HKCU\Control Panel\Mouse", "MouseThreshold1"),
    (r"HKCU\Control Panel\Mouse", "MouseThreshold2"),
    (r"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher"),
    (r"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled"),
    (r"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting"),
]
TRACKED_SERVICES = ["DiagTrack", "dmwappushservice", "MapsBroker", "lfsvc", "SysMain", "WSearch"]
TRACKED_BCD = ["disabledynamictick", "useplatformclock"]


def run(args, shell=False, timeout=120):
    try:
        result = subprocess.run(
            args,
            shell=shell,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            **no_window_kwargs(),
        )
        out = ((result.stdout or "") + (result.stderr or "")).strip()
        return result.returncode == 0, out
    except Exception as exc:
        return False, str(exc)


REG_VALUE_RE = re.compile(r"^(.+?)\s+(REG_[A-Za-z0-9_]+)\s*(.*)$")


def parse_reg_values(output):
    """Le o output de `reg query` e devolve [(nome, tipo, dados), ...].

    O separador é o token REG_*, por isso nomes com espaços
    (ex.: "GPU Priority", "Scheduling Category") tambem sao reconhecidos.
    """
    values = []
    for raw in (output or "").splitlines():
        line = raw.strip()
        if not line or line.lower().startswith("hkey"):
            continue
        match = REG_VALUE_RE.match(line)
        if not match:
            continue
        values.append((match.group(1).strip(), match.group(2), match.group(3).strip()))
    return values


def parse_reg_query(output, name):
    for value_name, value_type, data in parse_reg_values(output):
        if value_name.lower() == name.lower():
            return {"type": value_type, "data": data}
    return None


def dword_to_str(value):
    if isinstance(value, str) and value.lower().startswith("0x"):
        return str(int(value, 16))
    return str(value)


def ps_json(command, timeout=90):
    _fd, temp = tempfile.mkstemp(prefix="mago_ps_", suffix=".json", dir=os.environ.get("TEMP") or APP_DIR)
    os.close(_fd)
    script = f"{command} | ConvertTo-Json -Depth 5 -Compress | Out-File -FilePath '{temp}' -Encoding utf8"
    ok, out = run(["powershell", "-NoProfile", "-Command", script], timeout=timeout)
    data = None
    if os.path.exists(temp):
        try:
            with open(temp, "r", encoding="utf-8-sig") as handle:
                raw = handle.read().strip()
            if raw:
                data = json.loads(raw)
        except Exception:
            data = None
        try:
            os.remove(temp)
        except Exception:
            pass
    if isinstance(data, dict):
        data = [data]
    return ok, data or [], out


SPEEDTEST_DOWN_URL = "https://speed.cloudflare.com/__down"
SPEEDTEST_UP_URL = "https://speed.cloudflare.com/__up"
SPEEDTEST_AGENT = "MagoDaPerformance/6.0"
PING_HOST_DEFAULT = "1.1.1.1"
PING_TIME_RE = re.compile(
    r"(?:time|tempo|tiempo|zeit|tijd|temps|temps de|время|시간)[=<]\s*([0-9]+(?:[.,][0-9]+)?)\s*ms",
    re.IGNORECASE,
)
PING_REPLY_RE = re.compile(r"TTL=\d+", re.IGNORECASE)
PING_TIME_FALLBACK_RE = re.compile(r"[=<]\s*([0-9]+(?:[.,][0-9]+)?)\s*ms", re.IGNORECASE)


def parse_ping_times(output):
    """Latências (ms) das respostas do `ping`, em pt/en/es/de/fr.

    O `ping.exe` é localizado, por isso procuramos a chave antes do valor
    ("time=11ms", "tempo<1ms") em vez de traduzir a linha inteira. Se a linha
    tem TTL (presente em qualquer idioma) mas o rótulo não é conhecido,
    aceitamos o primeiro "<n> ms" da linha — sem isto um Windows em russo ou
    italiano marcava 100% de perda numa ligação boa.
    """
    times = []
    for line in (output or "").splitlines():
        match = PING_TIME_RE.search(line)
        if not match and PING_REPLY_RE.search(line):
            match = PING_TIME_FALLBACK_RE.search(line)
        if not match:
            continue
        try:
            times.append(float(match.group(1).replace(",", ".")))
        except ValueError:
            continue
    return times


def summarize_latency(times, sent):
    """min/média/máx/jitter/perda a partir das amostras de um ping."""
    samples = [t for t in (times or []) if t is not None]
    sent = max(int(sent or 0), len(samples))
    if not samples:
        return {"sent": sent, "received": 0, "loss": 100.0, "min": None, "avg": None, "max": None, "jitter": None}
    jitter = 0.0
    if len(samples) > 1:
        jitter = sum(abs(b - a) for a, b in zip(samples, samples[1:])) / (len(samples) - 1)
    return {
        "sent": sent,
        "received": len(samples),
        "loss": round((sent - len(samples)) * 100.0 / sent, 1) if sent else 0.0,
        "min": round(min(samples), 1),
        "avg": round(sum(samples) / len(samples), 1),
        "max": round(max(samples), 1),
        "jitter": round(jitter, 1),
    }


def bits_per_second(total_bytes, seconds):
    """Converte bytes/segundos medidos em bits por segundo."""
    if not total_bytes or not seconds or seconds <= 0:
        return 0.0
    return total_bytes * 8.0 / seconds


def format_mbps(bps):
    """bits/s para texto curto, ex.: "84 Mbps" ou "9.4 Mbps"."""
    if not bps or bps <= 0:
        return "--"
    mbps = bps / 1e6
    return f"{mbps:.0f} Mbps" if mbps >= 100 else f"{mbps:.1f} Mbps"


def _open_http_stream(url, timeout=12, method="GET", body=None, headers=None):
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https" or not parts.hostname:
        raise ValueError(f"URL de teste inválida: {url}")
    connection = http.client.HTTPSConnection(parts.hostname, parts.port, timeout=timeout)
    path = parts.path or "/"
    if parts.query:
        path += "?" + parts.query
    request_headers = {"User-Agent": SPEEDTEST_AGENT, "Accept": "*/*", "Cache-Control": "no-store"}
    request_headers.update(headers or {})
    try:
        connection.request(method, path, body=body, headers=request_headers)
        return connection, connection.getresponse()
    except Exception:
        connection.close()
        raise


def measure_download(max_bytes=25_000_000, max_seconds=10.0, settle_seconds=0.75, timeout=12,
                     window_seconds=2.0, max_chunks=4):
    """Baixa blocos de `max_bytes` até ter `window_seconds` de amostra.

    Devolve (bytes, segundos) da janela medida. Um único bloco pequeno dava
    números falsos: numa ligação rápida ele termina antes de a janela arrancar
    e sobravam centésimos de segundo — chegou a marcar o dobro da velocidade
    real. Por isso descartamos a rampa inicial (TCP/TLS) e só paramos quando há
    uma janela estável, somando tantos blocos quantos forem precisos.
    """
    total = 0
    base_bytes = 0
    base_time = None
    started = time.perf_counter()
    for _ in range(max(1, int(max_chunks))):
        connection, response = _open_http_stream(
            f"{SPEEDTEST_DOWN_URL}?bytes={int(max_bytes)}", timeout=timeout
        )
        try:
            if response.status >= 400:
                raise RuntimeError(f"HTTP {response.status} do servidor de teste")
            while True:
                chunk = response.read(262144)
                if not chunk:
                    break
                total += len(chunk)
                elapsed = time.perf_counter() - started
                if base_time is None and elapsed >= settle_seconds:
                    base_time = elapsed
                    base_bytes = total
                if base_time is not None and (elapsed - base_time) >= window_seconds:
                    return total - base_bytes, elapsed - base_time
                if elapsed >= max_seconds:
                    break
        finally:
            connection.close()
        if (time.perf_counter() - started) >= max_seconds:
            break
    elapsed = time.perf_counter() - started
    if base_time is None or total <= base_bytes:
        return total, max(elapsed, 1e-3)
    return total - base_bytes, elapsed - base_time


def measure_upload(max_bytes=4_000_000, timeout=40, window_seconds=2.0, max_chunks=6):
    """Envia blocos de `max_bytes` e devolve (bytes, segundos) do período medido.

    Reaproveita a ligação depois de um envio pequeno de aquecimento: o
    handshake TCP/TLS não entra na conta. Também mede vários blocos, porque um
    envio curto fica retido no buffer do sistema e o tempo medido ficava menor
    que a transferência real (numa ligação rápida, o dobro da velocidade).
    """
    chunk = max(int(max_bytes), 1_000_000)
    payload = b"\x00" * chunk
    headers = {
        "Content-Type": "application/octet-stream",
        "User-Agent": SPEEDTEST_AGENT,
        "Accept": "*/*",
        "Cache-Control": "no-store",
    }
    connection = http.client.HTTPSConnection("speed.cloudflare.com", 443, timeout=timeout)
    try:
        connection.request("POST", "/__up", body=b"\x00" * 1024, headers=headers)
        connection.getresponse().read()
        total = 0
        started = time.perf_counter()
        for _ in range(max(1, int(max_chunks))):
            connection.request("POST", "/__up", body=payload, headers=headers)
            response = connection.getresponse()
            response.read()
            if response.status >= 400:
                raise RuntimeError(f"HTTP {response.status} do servidor de teste")
            total += chunk
            if (time.perf_counter() - started) >= window_seconds:
                break
    finally:
        connection.close()
    return total, max(time.perf_counter() - started, 1e-3)


def diagnose_network(ping=None, gateway=None, down_bps=None, up_bps=None):
    """Frase curta de diagnóstico cruzando ping local, internet e velocidade."""
    notes = []
    local = gateway or {}
    internet = ping or {}
    if local.get("loss") is not None and local["loss"] >= 1:
        notes.append("já há perda de pacotes no router: cabo, Wi-Fi ou placa de rede")
    elif local.get("avg") is not None and local["avg"] > 8:
        notes.append("latência alta até ao router: o problema começa na rede local")
    if internet.get("loss") is not None and internet["loss"] >= 5:
        notes.append("perda de pacotes para a internet: ligação instável")
    elif internet.get("avg") is not None and internet["avg"] >= 80:
        notes.append("latência alta para a internet com rede local limpa: problema fora de casa (ISP/rota)")
    if internet.get("jitter") is not None and internet["jitter"] >= 15:
        notes.append("jitter elevado: mau para jogos e chamadas")
    if down_bps:
        mbps = down_bps / 1e6
        if mbps < 20:
            notes.append(f"download de {mbps:.0f} Mbps é pouco para 4K e atualizações grandes")
    if up_bps:
        mbps = up_bps / 1e6
        if mbps < 3:
            notes.append(f"upload de {mbps:.1f} Mbps limita chamadas e backups na nuvem")
    if notes:
        return "Atenção — " + "; ".join(notes) + "."
    if internet.get("avg") is not None:
        return "Ligação saudável: latência baixa, sem perda relevante e velocidade estável."
    return "Sem medições suficientes para diagnosticar."


DNS_PROVIDERS = (
    {"name": "Cloudflare", "primary": "1.1.1.1", "secondary": "1.0.0.1"},
    {"name": "Google", "primary": "8.8.8.8", "secondary": "8.8.4.4"},
    {"name": "Quad9", "primary": "9.9.9.9", "secondary": "149.112.112.112"},
    {"name": "OpenDNS", "primary": "208.67.222.222", "secondary": "208.67.220.220"},
    {"name": "AdGuard", "primary": "94.140.14.14", "secondary": "94.140.15.15"},
)
DNS_PROBE_NAMES = (
    "www.google.com",
    "www.cloudflare.com",
    "www.microsoft.com",
    "www.amazon.com",
    "www.wikipedia.org",
)
DNS_AUTO_LABEL = "Automático (DHCP)"


def split_dns_servers(text):
    """Lista de servidores a partir de "1.1.1.1, 1.0.0.1" (ou com ";")."""
    return [part.strip() for part in str(text or "").replace(";", ",").split(",") if part.strip()]


def build_dns_query(name, txid=None):
    """Pacote UDP de consulta A para `name`; devolve (bytes, id)."""
    labels = [label for label in str(name or "").strip(".").split(".") if label]
    if not labels:
        raise ValueError("nome DNS vazio")
    if txid is None:
        txid = random.randrange(0, 65536)
    qname = b"".join(bytes([len(label)]) + label.encode("idna") for label in labels) + b"\x00"
    packet = struct.pack(">HHHHHH", txid, 0x0100, 1, 0, 0, 0) + qname + struct.pack(">HH", 1, 1)
    return packet, txid


def dns_probe(server, name, timeout=1.2):
    """Latência (ms) de uma consulta A feita diretamente ao resolvedor.

    Só conta como resposta o pacote com o mesmo id, sem erro e com registos:
    um NXDOMAIN ou um pacote de outro pedido não passam por bom resultado.
    """
    try:
        packet, txid = build_dns_query(name)
    except (ValueError, UnicodeError):
        return {"ok": False, "ms": None, "rcode": None}
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(timeout)
    try:
        started = time.perf_counter()
        sock.sendto(packet, (server, 53))
        while True:
            data, _addr = sock.recvfrom(2048)
            if len(data) < 12:
                continue
            rid, flags, _qd, answers, _ns, _ar = struct.unpack(">HHHHHH", data[:12])
            if rid != txid:
                continue
            elapsed = (time.perf_counter() - started) * 1000.0
            rcode = flags & 0x000F
            return {"ok": rcode == 0 and answers > 0, "ms": elapsed, "rcode": rcode}
    except (socket.timeout, TimeoutError, OSError):
        return {"ok": False, "ms": None, "rcode": None}
    finally:
        sock.close()


def dns_candidates(current_ip=""):
    """Resolvedores a testar; o DNS atual entra primeiro se não for um deles."""
    current_ip = (current_ip or "").strip()
    candidates = []
    if current_ip and not any(current_ip in (p["primary"], p["secondary"]) for p in DNS_PROVIDERS):
        candidates.append({"name": "DNS atual", "primary": current_ip, "secondary": "", "is_current": True})
    for provider in DNS_PROVIDERS:
        item = dict(provider)
        item["is_current"] = bool(current_ip) and current_ip in (provider["primary"], provider["secondary"])
        candidates.append(item)
    return candidates


def median_ms(values):
    """Mediana de uma lista de latências (mais estável que a média com picos)."""
    ordered = sorted(value for value in (values or []) if value is not None)
    if not ordered:
        return None
    middle = len(ordered) // 2
    if len(ordered) % 2:
        return round(ordered[middle], 1)
    return round((ordered[middle - 1] + ordered[middle]) / 2.0, 1)


def benchmark_dns(candidates=None, names=None, timeout=1.2):
    """Mede a latência de cada resolvedor e ordena do melhor para o pior.

    Cada servidor leva uma consulta de aquecimento (descartada) e depois uma
    por nome; a latência reportada é a mediana, para um pico isolado não mudar
    o ranking. Sem resposta logo na primeira, é marcado como indisponível e não
    gastamos os restantes timeouts — há redes que bloqueiam DNS externo.
    """
    candidates = list(candidates or dns_candidates())
    names = list(names or DNS_PROBE_NAMES)
    results = []
    for item in candidates:
        entry = dict(item)
        warm = dns_probe(item["primary"], names[0], timeout)
        if not warm["ok"]:
            entry.update({"ok": False, "median": None, "loss": 100.0, "samples": []})
            results.append(entry)
            continue
        samples = []
        failures = 0
        for name in names:
            probe = dns_probe(item["primary"], name, timeout)
            if probe["ok"] and probe["ms"] is not None:
                samples.append(probe["ms"])
            else:
                failures += 1
        entry.update(
            {
                "ok": bool(samples),
                "median": median_ms(samples),
                "loss": round(failures * 100.0 / len(names), 0) if names else 0.0,
                "samples": [round(value, 1) for value in samples],
            }
        )
        results.append(entry)
    results.sort(
        key=lambda item: (
            not item["ok"],
            item["loss"],
            item["median"] if item["median"] is not None else 1e9,
        )
    )
    return results


def format_dns_ranking(results):
    """Ranking em texto, uma linha por resolvedor, do melhor para o pior."""
    lines = []
    for index, item in enumerate(results or [], 1):
        mark = "  <- atual" if item.get("is_current") else ""
        if item.get("ok"):
            lines.append(
                f"{index}. {item['name']:<12} {item['primary']:<15} "
                f"{item['median']:>6.1f} ms   perda {item['loss']:.0f}%{mark}"
            )
        else:
            lines.append(f"{index}. {item['name']:<12} {item['primary']:<15} sem resposta{mark}")
    return "\n".join(lines)


def dns_recommendation(results):
    """Frase curta com o melhor resolvedor e a diferença para o DNS atual."""
    usable = [item for item in (results or []) if item.get("ok") and item.get("median") is not None]
    if not usable:
        return "Nenhum resolvedor respondeu — a rede pode estar a bloquear consultas DNS externas."
    best = usable[0]
    current = next((item for item in usable if item.get("is_current")), None)
    text = f"Melhor desempenho: {best['name']} ({best['primary']}) com {best['median']:.1f} ms."
    if current is None:
        return text
    if current is best:
        return text + " É o DNS que já estás a usar."
    return text + f" O DNS atual ({current['primary']}) responde em {current['median']:.1f} ms."


def dns_choices(results=None):
    """Rótulos da lista de troca rápida de DNS."""
    choices = [DNS_AUTO_LABEL]
    for item in (results or dns_candidates()):
        if not item.get("primary"):
            continue
        if item.get("name") == "DNS atual":
            choices.append(f"{item['name']} ({item['primary']})")
            continue
        label = f"{item['name']} — {item['primary']}"
        if item.get("secondary"):
            label += f" / {item['secondary']}"
        choices.append(label)
    return choices


def dns_choice_provider(choice, results=None):
    """Rótulo escolhido -> provedor a aplicar (None = voltar a DHCP)."""
    choice = (choice or "").strip()
    if not choice or choice.startswith("Automático"):
        return None
    for item in (results or dns_candidates()):
        if item.get("primary") and choice.startswith(item["name"]):
            return {"name": item["name"], "primary": item["primary"], "secondary": item.get("secondary", "")}
    return None


def dns_change_commands(alias, primary, secondary=""):
    """Comandos `netsh` para fixar (ou limpar) o DNS de um adaptador."""
    alias = (alias or "").strip()
    if not alias:
        raise ValueError("adaptador vazio")
    target = f"name={alias}"
    if not primary:
        return [["netsh", "interface", "ipv4", "set", "dns", target, "dhcp"]]
    commands = [["netsh", "interface", "ipv4", "set", "dns", target, "static", primary, "primary"]]
    if secondary:
        commands.append(["netsh", "interface", "ipv4", "add", "dns", target, secondary, "index=2"])
    return commands


def pspath_to_reg(pspath):
    if not pspath:
        return ""
    text = pspath
    if "Registry::" in text:
        text = text.split("Registry::", 1)[1]
    text = text.replace("Microsoft.PowerShell.Core\\Registry::", "")
    text = text.replace("HKEY_LOCAL_MACHINE", "HKLM").replace("HKEY_CURRENT_USER", "HKCU")
    return text


def is_safe_delete_path(path):
    if not path:
        return False
    norm = os.path.normpath(path)
    if len(norm) < 12:
        return False
    low = norm.lower()
    windir = os.path.normpath(os.environ.get("WINDIR", r"C:\Windows")).lower()
    if low == windir or low.startswith(windir + os.sep):
        return False
    roots = [
        os.path.normpath(os.environ.get("ProgramFiles", r"C:\Program Files")).lower(),
        os.path.normpath(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")).lower(),
        os.path.normpath(os.environ.get("LOCALAPPDATA", "")).lower(),
        os.path.normpath(os.environ.get("ProgramData", r"C:\ProgramData")).lower(),
    ]
    for root in roots:
        if not root:
            continue
        if low == root:
            return False
        if low.startswith(root + os.sep) and len(low) > len(root) + 4:
            return True
    return False


def vendor_support_url(maker, serial, model, board):
    blob = f"{maker} {model} {board}".lower()
    serial = (serial or "").strip()
    query = serial or board or model or maker
    if "dell" in blob:
        return f"https://www.dell.com/support/home/pt-br/product-support/servicetag/{serial}" if serial else "https://www.dell.com/support"
    if "lenovo" in blob:
        return f"https://pcsupport.lenovo.com/us/en/search?query={query}"
    if "hp" in blob or "hewlett" in blob:
        return f"https://support.hp.com/pt-pt/search?q={query}"
    if "asus" in blob:
        return f"https://www.asus.com/support/search/?SearchKey={board or model}"
    if "msi" in blob:
        return f"https://www.msi.com/search/{board or model}"
    if "gigabyte" in blob or "aorus" in blob:
        return f"https://www.gigabyte.com/Search?keyword={board or model}"
    if "asrock" in blob:
        return f"https://www.asrock.com/support/index.asp"
    if "acer" in blob:
        return f"https://www.acer.com/support"
    if "microsoft" in blob:
        return "https://support.microsoft.com/surface"
    return f"https://www.google.com/search?q={maker}+{board or model}+BIOS+update"


class BackupStore:
    def __init__(self, path):
        self.path = path
        self.lock = threading.Lock()
        self.data = self._load()

    def empty(self):
        return {"created": None, "registry": {}, "services": {}, "bcd": {}, "power_scheme": None, "startup": {}, "notes": []}

    def _load(self):
        if not os.path.exists(self.path):
            return self.empty()
        try:
            with open(self.path, "r", encoding="utf-8") as handle:
                data = json.load(handle)
            base = self.empty()
            base.update(data)
            for key in ("registry", "services", "bcd", "startup"):
                base.setdefault(key, {})
            return base
        except Exception:
            return self.empty()

    def save(self):
        with self.lock:
            if not self.data.get("created"):
                self.data["created"] = datetime.now().isoformat(timespec="seconds")
            os.makedirs(os.path.dirname(self.path) or ".", exist_ok=True)
            with open(self.path, "w", encoding="utf-8") as handle:
                json.dump(self.data, handle, ensure_ascii=False, indent=2)

    def has_backup(self):
        return bool(
            self.data.get("registry")
            or self.data.get("services")
            or self.data.get("bcd")
            or self.data.get("power_scheme")
            or self.data.get("startup")
        )

    def remember_reg(self, key, name, snapshot, overwrite=False):
        token = f"{key}::{name}"
        with self.lock:
            if overwrite or token not in self.data["registry"]:
                self.data["registry"][token] = snapshot

    def remember_service(self, name, snapshot, overwrite=False):
        token = name.lower()
        with self.lock:
            if overwrite or token not in self.data["services"]:
                self.data["services"][token] = snapshot

    def remember_bcd(self, name, snapshot, overwrite=False):
        with self.lock:
            if overwrite or name not in self.data["bcd"]:
                self.data["bcd"][name] = snapshot

    def remember_power(self, guid, overwrite=False):
        with self.lock:
            if overwrite or not self.data.get("power_scheme"):
                if guid:
                    self.data["power_scheme"] = guid

    def reset(self):
        with self.lock:
            self.data = self.empty()


class MagoDaPerformanceApp:
    ULTIMATE_GUID = "e9a42b02-d5df-448d-aa00-03f14749eb61"

    def __init__(self, root):
        self.root = root
        self.root.title("MAGO DA PERFORMANCE v6.0")
        self.root.geometry("1280x860")
        self.root.minsize(1100, 740)
        self.root.configure(bg="#07090E")

        self.backup = BackupStore(BACKUP_FILE)
        self.busy = False
        self._busy_lock = threading.Lock()
        self.gpu_name = ""
        self.disk_types = []
        self.hw_text = "A analisar hardware..."
        self.bios_info = {}
        self.apps_all = []
        self.mode = tk.StringVar(value="balanced")
        self.status_var = tk.StringVar(value="Pronto")
        self.search_var = tk.StringVar()
        self.busy_widgets = []
        self.nav_btns = {}
        self.pages = {}
        self.startup_loaded = False
        self.startup_loading = False
        self.fps_running = False
        self.fps_process = None
        self.fps_reader_thread = None
        self.fps_generation = 0
        self.fps_overlay_var = None
        fps_prefs = self._load_fps_preferences()
        self.fps_game_var = tk.StringVar(value=fps_prefs.get("game", ""))
        self.fps_status_var = tk.StringVar(value="Atualize a lista e selecione um jogo aberto.")
        default_presentmon = shutil.which("PresentMon-2.6.0-x64.exe") or shutil.which("PresentMon.exe") or ""
        preferred_presentmon = fps_prefs.get("presentmon", "")
        self.fps_presentmon_var = tk.StringVar(value=preferred_presentmon if isinstance(preferred_presentmon, str) and os.path.isfile(preferred_presentmon) else default_presentmon)
        self.fps_overlay_enabled = bool(fps_prefs.get("overlay", True))
        saved_position = fps_prefs.get("overlay_position", [30, 30])
        self.fps_overlay_position = saved_position if isinstance(saved_position, list) and len(saved_position) == 2 and all(isinstance(v, int) for v in saved_position) else [30, 30]
        self.fps_overlay_position = [max(0, min(v, 10000)) for v in self.fps_overlay_position]
        self.fps_show_avg = bool(fps_prefs.get("show_avg", True))
        self.fps_avg_var = tk.BooleanVar(value=self.fps_show_avg)
        self.fps_overlay_var = tk.BooleanVar(value=self.fps_overlay_enabled)
        self.fps_targets = {}
        self.fps_current = 0.0
        self.fps_game_started_at = 0.0
        self.fps_game_seen = False
        self.fps_game_missing_checks = 0
        self.fps_average = 0.0
        self.fps_samples = []
        self.fps_overlay = None
        self.fps_overlay_value = None
        self.fps_overlay_game = None
        self.net_stats = {}
        self.net_last = {}
        self.net_info_loading = False
        self.net_host_var = tk.StringVar(value=PING_HOST_DEFAULT)
        self.net_status_var = tk.StringVar(value="Corre o teste completo para medir latência e velocidade.")
        self.dns_results = []
        self.dns_best = None
        self.dns_choice_var = tk.StringVar(value="")
        self.dns_status_var = tk.StringVar(
            value="Corre a análise DNS para ver qual resolvedor responde mais rápido e podes trocar num clique."
        )

        self.create_styles()
        self.create_shell()
        self.log("=== MAGO DA PERFORMANCE v6.0 INICIADO COM ADMIN ===")
        threading.Thread(target=self.detect_hardware_auto, daemon=True).start()
        threading.Thread(target=self.load_bios_info, daemon=True).start()
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.root.after(400, self.tick_meters)
        self.root.after(800, lambda: self.show_page("painel"))

    def create_styles(self):
        self.bg = "#07090E"
        self.sidebar_bg = "#0B0F16"
        self.card = "#121821"
        self.card_alt = "#171E28"
        self.green = "#00FF9C"
        self.purple = "#B56CFF"
        self.red = "#FF2E63"
        self.cyan = "#3CE7FF"
        self.gold = "#FFD166"
        self.text = "#E8EEF5"
        self.dim = "#8B9BB0"
        self.line = "#232C3A"

        style = ttk.Style()
        style.theme_use("clam")
        style.configure(
            "Mago.Treeview",
            background="#0C1118",
            foreground=self.text,
            fieldbackground="#0C1118",
            rowheight=28,
            bordercolor=self.line,
            font=("Segoe UI", 9),
        )
        style.configure(
            "Mago.Treeview.Heading",
            background="#151C27",
            foreground=self.cyan,
            relief="flat",
            font=("Segoe UI", 9, "bold"),
        )
        style.map("Mago.Treeview", background=[("selected", "#3A1F6E")], foreground=[("selected", "#FFFFFF")])

    def ghost_btn(self, parent, text, command, bg="#1B2430", fg="#FFFFFF", pady=8):
        btn = tk.Button(
            parent,
            text=text,
            command=command,
            font=("Segoe UI", 9, "bold"),
            fg=fg,
            bg=bg,
            activebackground="#2A3545",
            activeforeground=self.green,
            bd=0,
            pady=pady,
            cursor="hand2",
            wraplength=220,
            justify="center",
        )
        self.busy_widgets.append(btn)
        return btn

    def log(self, message, level="INFO"):
        timestamp = datetime.now().strftime("%H:%M:%S")
        line = f"[{timestamp}] [{level}] {message}"

        def _update():
            self.console.config(state="normal")
            self.console.insert(tk.END, line + "\n")
            self.console.see(tk.END)
            self.console.config(state="disabled")

        self.ui(_update)
        try:
            with open(LOG_FILE, "a", encoding="utf-8") as handle:
                handle.write(f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}] [{level}] {message}\n")
        except Exception:
            pass

    def ui(self, fn):
        try:
            self.root.after(0, fn)
        except (RuntimeError, tk.TclError):
            pass

    def ask_yesno(self, title, msg):
        if threading.current_thread() is threading.main_thread():
            return messagebox.askyesno(title, msg)
        box = {"v": False}
        done = threading.Event()

        def _ask():
            box["v"] = messagebox.askyesno(title, msg)
            done.set()

        self.ui(_ask)
        done.wait(timeout=180)
        return box["v"]

    def set_busy(self, busy, label=None):
        with self._busy_lock:
            self.busy = busy
        self._apply_busy_ui(busy, label)

    def claim_busy(self, label=None):
        """Marca a app como ocupada de forma atomica.

        Devolve False se ja esta ocupada — evita que dois cliques rapidos
        lancem o mesmo módulo duas vezes.
        """
        with self._busy_lock:
            if self.busy:
                return False
            self.busy = True
        self._apply_busy_ui(True, label)
        return True

    def _apply_busy_ui(self, busy, label=None):
        def _apply():
            state = "disabled" if busy else "normal"
            for widget in self.busy_widgets:
                try:
                    widget.config(state=state)
                except Exception:
                    pass
            self.status_var.set(label or ("A trabalhar..." if busy else "Pronto"))

        self.ui(_apply)

    def report(self, ok, success_msg, fail_msg, detail=""):
        extra = f" | {detail[:240]}" if detail else ""
        if ok:
            self.log(success_msg + extra)
            return True
        self.log(fail_msg + extra, "ERRO")
        return False

    def create_shell(self):
        top = tk.Frame(self.root, bg="#0E131C", height=64)
        top.pack(fill="x")
        top.pack_propagate(False)
        tk.Label(top, text="MAGO", font=("Segoe UI", 18, "bold"), fg=self.purple, bg="#0E131C").pack(side="left", padx=(22, 4))
        tk.Label(top, text="DA PERFORMANCE", font=("Segoe UI", 18, "bold"), fg=self.text, bg="#0E131C").pack(side="left")
        tk.Label(top, text="  v6.0  CONTROL CENTER", font=("Segoe UI", 9), fg=self.dim, bg="#0E131C").pack(side="left", padx=8)

        meters = tk.Frame(top, bg="#0E131C")
        meters.pack(side="right", padx=18)
        self.lbl_cpu = tk.Label(meters, text="CPU --%", font=("Consolas", 10, "bold"), fg=self.green, bg="#0E131C")
        self.lbl_ram = tk.Label(meters, text="RAM --%", font=("Consolas", 10, "bold"), fg=self.cyan, bg="#0E131C")
        self.lbl_disk = tk.Label(meters, text="C: --%", font=("Consolas", 10, "bold"), fg=self.gold, bg="#0E131C")
        self.lbl_status = tk.Label(meters, textvariable=self.status_var, font=("Segoe UI", 9), fg=self.dim, bg="#0E131C")
        self.lbl_cpu.pack(side="left", padx=10)
        self.lbl_ram.pack(side="left", padx=10)
        self.lbl_disk.pack(side="left", padx=10)
        self.lbl_status.pack(side="left", padx=14)

        body = tk.Frame(self.root, bg=self.bg)
        body.pack(fill="both", expand=True)

        side = tk.Frame(body, bg=self.sidebar_bg, width=214)
        side.pack(side="left", fill="y")
        side.pack_propagate(False)
        tk.Frame(side, bg=self.purple, width=3).pack(side="right", fill="y")

        nav = [
            ("painel", "PAINEL"),
            ("otimizar", "OTIMIZAR"),
            ("rede", "REDE"),
            ("apps", "APLICATIVOS"),
            ("backup", "BACKUP"),
            ("bios", "BIOS"),
            ("ferramentas", "FERRAMENTAS"),
        ]
        for key, label in nav:
            btn = tk.Button(
                side,
                text=label,
                command=lambda k=key: self.show_page(k),
                font=("Segoe UI", 11, "bold"),
                fg=self.dim,
                bg=self.sidebar_bg,
                activebackground="#151C27",
                activeforeground=self.green,
                bd=0,
                pady=16,
                anchor="w",
                padx=24,
                cursor="hand2",
            )
            btn.pack(fill="x")
            self.nav_btns[key] = btn

        tk.Label(side, text="ADMIN ATIVO", font=("Segoe UI", 8, "bold"), fg=self.green, bg=self.sidebar_bg).pack(
            side="bottom", pady=16
        )

        right = tk.Frame(body, bg=self.bg)
        right.pack(side="left", fill="both", expand=True)

        self.content = tk.Frame(right, bg=self.bg)
        self.content.pack(fill="both", expand=True, padx=16, pady=(14, 8))

        log_wrap = tk.Frame(right, bg=self.bg)
        log_wrap.pack(fill="x", padx=16, pady=(0, 12))
        tk.Label(log_wrap, text="CONSOLA", font=("Segoe UI", 8, "bold"), fg=self.dim, bg=self.bg).pack(anchor="w")
        self.console = scrolledtext.ScrolledText(
            log_wrap,
            bg="#05070B",
            fg=self.green,
            font=("Consolas", 9),
            state="disabled",
            height=8,
            bd=0,
            highlightthickness=1,
            highlightbackground=self.line,
        )
        self.console.pack(fill="x")

        self.build_page_painel()
        self.build_page_otimizar()
        self.build_page_rede()
        self.build_page_apps()
        self.build_page_backup()
        self.build_page_bios()
        self.build_page_ferramentas()

    def on_close(self):
        if self.fps_running:
            self.stop_fps_counter()
        self.close_fps_overlay()
        self.root.destroy()

    def show_page(self, name):
        for page in self.pages.values():
            page.pack_forget()
        self.pages[name].pack(fill="both", expand=True)
        for key, btn in self.nav_btns.items():
            active = key == name
            btn.config(fg=self.green if active else self.dim, bg="#151C27" if active else self.sidebar_bg)
        if name == "apps" and not self.apps_all:
            threading.Thread(target=self.refresh_apps, daemon=True).start()
        if name == "rede":
            self.refresh_net_info()
        if name == "backup":
            self.refresh_backup_labels()
        if name == "ferramentas" and not self.startup_loaded and not self.startup_loading:
            self.list_startup()
        if name == "bios" and not self.bios_info:
            threading.Thread(target=self.load_bios_info, daemon=True).start()

    def card_frame(self, parent, title):
        box = tk.Frame(parent, bg=self.card, highlightbackground=self.line, highlightthickness=1)
        tk.Label(box, text=title, font=("Segoe UI", 10, "bold"), fg=self.cyan, bg=self.card, anchor="w").pack(
            fill="x", padx=12, pady=(10, 6)
        )
        return box

    def build_page_painel(self):
        page = tk.Frame(self.content, bg=self.bg)
        self.pages["painel"] = page
        tk.Label(page, text="Painel do sistema", font=("Segoe UI", 16, "bold"), fg=self.text, bg=self.bg).pack(anchor="w")
        tk.Label(
            page,
            text="Visão rápida do hardware, BIOS e atalhos de manutenção.",
            font=("Segoe UI", 9),
            fg=self.dim,
            bg=self.bg,
        ).pack(anchor="w", pady=(0, 10))

        grid = tk.Frame(page, bg=self.bg)
        grid.pack(fill="both", expand=True)
        grid.columnconfigure(0, weight=1)
        grid.columnconfigure(1, weight=1)

        hw = self.card_frame(grid, "HARDWARE")
        hw.grid(row=0, column=0, sticky="nsew", padx=(0, 8), pady=6)
        self.lbl_hw_info = tk.Label(hw, text=self.hw_text, font=("Consolas", 9), fg=self.text, bg=self.card, justify="left", anchor="w")
        self.lbl_hw_info.pack(fill="x", padx=12, pady=(0, 12))

        bios = self.card_frame(grid, "BIOS / FIRMWARE")
        bios.grid(row=0, column=1, sticky="nsew", padx=(8, 0), pady=6)
        self.lbl_bios_dash = tk.Label(
            bios,
            text="A ler firmware...",
            font=("Consolas", 9),
            fg=self.text,
            bg=self.card,
            justify="left",
            anchor="w",
        )
        self.lbl_bios_dash.pack(fill="x", padx=12, pady=(0, 12))

        quick = self.card_frame(grid, "ATALHOS")
        quick.grid(row=1, column=0, columnspan=2, sticky="ew", pady=6)
        row = tk.Frame(quick, bg=self.card)
        row.pack(fill="x", padx=12, pady=(0, 12))
        for text, cmd, color in [
            ("Aplicar perfil equilibrado", self.apply_all, self.red),
            ("Guardar backup agora", self.capture_backup_now, self.purple),
            ("Ver atualização da BIOS", self.open_bios_update, self.cyan),
            ("Limpar temporários", lambda: self.start_module(self.clean_temp_and_ram), "#1F3A2E"),
        ]:
            self.ghost_btn(row, text, cmd, bg=color).pack(
                side="left", expand=True, fill="x", padx=4
            )

        fps = self.card_frame(grid, "FPS DO JOGO · PRESENTMON")
        fps.grid(row=2, column=0, columnspan=2, sticky="ew", pady=6)
        self.lbl_fps = tk.Label(
            fps,
            text="FPS do jogo: --",
            font=("Consolas", 14, "bold"),
            fg=self.gold,
            bg=self.card,
            anchor="w",
        )
        self.lbl_fps.pack(fill="x", padx=12, pady=(0, 4))
        options = tk.Frame(fps, bg=self.card)
        options.pack(fill="x", padx=12, pady=(0, 3))
        ttk.Checkbutton(
            options,
            text="Exibir média junto ao FPS",
            variable=self.fps_avg_var,
            command=self._toggle_fps_average,
        ).pack(side="left")
        ttk.Checkbutton(
            options,
            text="Mostrar overlay ao iniciar captura",
            variable=self.fps_overlay_var,
            command=self._toggle_fps_overlay_setting,
        ).pack(side="left", padx=14)

        tk.Label(
            fps,
            textvariable=self.fps_status_var,
            font=("Segoe UI", 8),
            fg=self.dim,
            bg=self.card,
            anchor="w",
            justify="left",
            wraplength=900,
        ).pack(fill="x", padx=12, pady=(0, 6))

        selector = tk.Frame(fps, bg=self.card)
        selector.pack(fill="x", padx=12, pady=3)
        tk.Label(selector, text="Jogo:", font=("Segoe UI", 9), fg=self.text, bg=self.card).pack(side="left")
        self.fps_game_combo = ttk.Combobox(
            selector, textvariable=self.fps_game_var, state="normal", style="Mago.Treeview", width=45
        )
        self.fps_game_combo.bind("<FocusOut>", lambda _event: self._save_fps_preferences())
        self.fps_game_combo.bind("<Return>", lambda _event: self._save_fps_preferences())
        self.fps_game_combo.pack(side="left", padx=8, fill="x", expand=True)
        self.ghost_btn(selector, "Atualizar jogos abertos", self.refresh_fps_targets, bg="#1B2430").pack(side="left")

        tool = tk.Frame(fps, bg=self.card)
        tool.pack(fill="x", padx=12, pady=3)
        tk.Label(tool, text="PresentMon:", font=("Segoe UI", 9), fg=self.text, bg=self.card).pack(side="left")
        tk.Entry(
            tool,
            textvariable=self.fps_presentmon_var,
            bg="#10161F",
            fg=self.text,
            insertbackground=self.text,
            bd=0,
            highlightthickness=1,
            highlightbackground=self.line,
            font=("Segoe UI", 9),
        ).pack(side="left", padx=8, fill="x", expand=True, ipady=5)
        self.ghost_btn(tool, "Localizar...", self.browse_presentmon, bg="#1B2430").pack(side="left")
        self.ghost_btn(tool, "Baixar PresentMon", self.open_presentmon_download, bg="#1B2430").pack(side="left", padx=(6, 0))

        row_fps = tk.Frame(fps, bg=self.card)
        row_fps.pack(fill="x", padx=12, pady=(5, 12))
        self.btn_fps = self.ghost_btn(
            row_fps, "INICIAR MEDIÇÃO", self.toggle_fps_counter, bg="#2A2410", fg=self.gold, pady=10
        )
        self.btn_fps.pack(side="left", expand=True, fill="x", padx=(0, 6))
        self.btn_overlay = self.ghost_btn(
            row_fps, "MOSTRAR OVERLAY", self.toggle_fps_overlay, bg="#1B2430", pady=10
        )
        self.btn_overlay.pack(side="left", expand=True, fill="x", padx=6)
        self.ghost_btn(row_fps, "Repor", self.reset_fps_counter, bg="#1B2430", pady=10).pack(
            side="left", expand=True, fill="x", padx=(6, 0)
        )

    FPS_REPORT_MS = 500
    FPS_GAME_NAME = re.compile(r"(?i)(?:^|[-_ ])(launcher|anti.?cheat|crash|report|helper|bootstrapper|setup|installer)(?:[-_ .]|$)")
    FPS_CSV_HEADER = None
    FPS_PREFERENCES_FILE = os.path.join(os.environ.get("LOCALAPPDATA") or APP_DIR, "MagoDaPerformance", "mago_fps_settings.json")

    @classmethod
    def _load_fps_preferences(cls):
        try:
            with open(cls.FPS_PREFERENCES_FILE, "r", encoding="utf-8") as handle:
                data = json.load(handle)
            return data if isinstance(data, dict) else {}
        except (OSError, json.JSONDecodeError):
            return {}

    def _save_fps_preferences(self):
        data = {
            "game": self.fps_game_var.get().strip(),
            "presentmon": self.fps_presentmon_var.get().strip(),
            "overlay": self.fps_overlay_enabled,
            "overlay_position": list(self.fps_overlay_position),
            "show_avg": self.fps_show_avg,
        }
        try:
            os.makedirs(os.path.dirname(self.FPS_PREFERENCES_FILE), exist_ok=True)
            with open(self.FPS_PREFERENCES_FILE, "w", encoding="utf-8") as handle:
                json.dump(data, handle, ensure_ascii=False, indent=2)
        except OSError as exc:
            self.fps_status_var.set(f"Não foi possível guardar preferências: {exc}")

    def _toggle_fps_average(self):
        self.fps_show_avg = bool(self.fps_avg_var.get())
        self._save_fps_preferences()
        self._update_overlay_display()

    def _toggle_fps_overlay_setting(self):
        self.fps_overlay_enabled = bool(self.fps_overlay_var.get())
        self._save_fps_preferences()
        if self.fps_overlay_enabled:
            selected = os.path.basename(self.fps_game_var.get().strip().strip('"'))
            self._show_fps_overlay(self.fps_targets.get(selected, {}).get("name", selected or "Mago FPS"))
        else:
            self.hide_fps_overlay()

    @staticmethod
    def fps_from_frame_times(frame_times):
        """FPS da média harmónica dos tempos de frame recentes (ms)."""
        valid = [value for value in frame_times if math.isfinite(value) and 0.1 <= value <= 1000.0]
        if not valid:
            return 0.0
        return len(valid) * 1000.0 / sum(valid)

    @staticmethod
    def parse_presentmon_csv_line(line):
        try:
            row = next(csv.reader([line]))
            if row and row[0].strip().lower() == "application":
                return {name.strip(): index for index, name in enumerate(row)}, None
            return None, row
        except (csv.Error, StopIteration):
            return None, None

    @staticmethod
    def presentmon_frame_time(row, header):
        try:
            index = header.get("frametime")
            if index is None:
                index = header.get("msbetweenpresents")
            if index is None or index >= len(row):
                return None
            value = float(row[index])
            return value if math.isfinite(value) and 0.1 <= value <= 1000.0 else None
        except (ValueError, TypeError, AttributeError):
            return None

    def open_presentmon_download(self):
        webbrowser.open("https://github.com/GameTechDev/PresentMon/releases/latest")

    def browse_presentmon(self):
        path = filedialog.askopenfilename(
            title="Selecionar PresentMon 2.6.0",
            filetypes=[("Executável PresentMon", "PresentMon-*-x64.exe"), ("Executável", "*.exe")],
        )
        if path:
            self.fps_presentmon_var.set(path)
            self.fps_status_var.set("PresentMon selecionado. Atualize os jogos abertos.")

    def refresh_fps_targets(self):
        self.fps_status_var.set("A procurar aplicações gráficas abertas...")
        threading.Thread(target=self._refresh_fps_targets_worker, daemon=True).start()

    def _refresh_fps_targets_worker(self):
        targets = {}
        try:
            for proc in psutil.process_iter(["pid", "name", "exe"]):
                try:
                    info = proc.info
                    name = os.path.basename((info.get("exe") or info.get("name") or "").strip())
                    if not name or name.lower() in {"system", "idle", "dwm.exe"}:
                        continue
                    if self.FPS_GAME_NAME.search(name):
                        continue
                    if os.path.splitext(name)[1].lower() != ".exe":
                        continue
                    targets[name] = {"name": name}
                except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess, TypeError, ValueError):
                    continue
        except Exception as exc:
            self.ui(lambda error=exc: self.fps_status_var.set(f"Não foi possível listar processos: {error}"))
            return
        self.fps_targets = targets
        values = list(targets)
        def update():
            self.fps_game_combo["values"] = values
            selected = os.path.basename(self.fps_game_var.get().strip().strip('"'))
            if not selected and values:                self.fps_game_var.set(values[0])
            elif selected in self.fps_targets:
                self.fps_game_var.set(selected)
            self.fps_status_var.set(
                f"{len(values)} executáveis encontrados. Selecione um ou digite o nome .exe para aguardar o jogo abrir."
                if values else "Nenhum executável listado. Digite o nome .exe do jogo (ex.: Game-Win64-Shipping.exe)."
            )
        self.ui(update)

    def toggle_fps_counter(self):
        if self.fps_running:
            self.stop_fps_counter()
        else:
            self.start_fps_counter()

    def start_fps_counter(self):
        if self.fps_running:
            return
        selected = os.path.basename(self.fps_game_var.get().strip().strip('"'))
        target = self.fps_targets.get(selected, {"name": selected})
        if not selected or not selected.lower().endswith(".exe"):
            self.fps_status_var.set("Selecione um processo ou digite o nome do executável do jogo terminando em .exe.")
            return
        executable = os.path.expandvars(os.path.expanduser(self.fps_presentmon_var.get().strip().strip('"')))
        if not executable or not os.path.isfile(executable):
            self.fps_status_var.set("PresentMon não encontrado. Baixe PresentMon 2.6.0 x64 e selecione PresentMon-2.6.0-x64.exe.")
            return
        self._save_fps_preferences()
        self.fps_generation += 1
        generation = self.fps_generation
        self.fps_running = True
        self.fps_process = None
        self.fps_current = 0.0
        self.fps_average = 0.0
        self.fps_samples = []
        self.fps_game_started_at = time.monotonic()
        self.fps_game_seen = False
        self.fps_game_missing_checks = 0
        self.fps_btn_text("PARAR MEDIÇÃO")
        self.fps_status_var.set(f"A iniciar captura de {target['name']}; se não estiver aberto, a captura aguardará o jogo.")
        try:
            process = subprocess.Popen(
                [executable, "--process_name", target["name"], "--output_stdout", "--v2_metrics"],
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                stdin=subprocess.DEVNULL,
                text=True,
                encoding="utf-8-sig",
                errors="replace",
                bufsize=1,
                **no_window_kwargs(),
            )
        except Exception as exc:
            self.fps_running = False
            self.fps_btn_text("INICIAR MEDIÇÃO")
            self.fps_status_var.set(f"Não foi possível iniciar PresentMon: {exc}")
            return
        self.fps_process = process
        self.fps_reader_thread = threading.Thread(
            target=self._read_presentmon, args=(process, target, generation), daemon=True
        )
        self.fps_reader_thread.start()
        if self.fps_overlay_enabled:
            self._show_fps_overlay(target["name"])
        if not self.fps_show_avg:
            self.fps_status_var.set(f"A iniciar captura de {target['name']}; se não estiver aberto, a captura aguardará o jogo.")
        self.root.after(4000, lambda: self._fps_wait_notice(generation, target["name"]))
        self.root.after(1500, lambda: self._monitor_fps_game(generation, target["name"]))
        self.log(f"Captura FPS iniciada para {target['name']}.")

    def _read_presentmon(self, process, target, generation):
        frame_times = []
        header = None
        headers_ok = False
        first_row = True
        last_ui_update = 0.0
        try:
            for line in process.stdout:
                if generation != self.fps_generation:
                    break
                parsed_header, row = self.parse_presentmon_csv_line(line.strip())
                if parsed_header is not None:
                    header = {name.lower(): index for name, index in parsed_header.items()}
                    headers_ok = "frametime" in header or "msbetweenpresents" in header
                    if not headers_ok:
                        self.ui(lambda: self._fps_diagnostic(generation, "CSV sem métrica de tempo de frame; use PresentMon 2.x."))
                    continue
                if row is None or header is None:
                    if line.strip() and not headers_ok:
                        detail = line.strip()[:180]
                        self.ui(lambda message=detail: self._fps_diagnostic(generation, message))
                    continue
                frame_ms = self.presentmon_frame_time(row, header)
                if frame_ms is None:
                    continue
                first_row = False
                frame_times.append(frame_ms)
                if len(frame_times) > 120:
                    del frame_times[:-120]
                now = time.monotonic()
                if now - last_ui_update >= 0.25 or len(frame_times) == 1:
                    fps = self.fps_from_frame_times(frame_times)
                    count = len(frame_times)
                    last_ui_update = now
                    self.ui(
                        lambda value=fps, sample_count=count, name=target["name"]: self._update_game_fps(
                            value, sample_count, name, generation
                        )
                    )
        except Exception as exc:
            self.ui(lambda error=exc: self.fps_status_var.set(f"Erro a ler PresentMon: {error}"))
        finally:
            code = process.poll()
            error = ""
            if generation == self.fps_generation and self.fps_running:
                self.ui(
                    lambda game=target["name"], result=code, got_header=headers_ok, no_rows=first_row: self._finish_fps_capture(
                        game, result, error, got_header, no_rows, generation
                    )
                )

    def _fps_diagnostic(self, generation, detail):
        if generation == self.fps_generation and self.fps_running:
            self.fps_status_var.set(f"PresentMon: {detail}")

    def _fps_wait_notice(self, generation, game_name):
        if generation == self.fps_generation and self.fps_running and self.fps_current <= 0:
            self.fps_status_var.set(
                f"Aguardando frames de {game_name}. Abra o jogo; se continuar sem dados, verifique permissões de captura/ETW."
            )

    def _monitor_fps_game(self, generation, game_name):
        if generation != self.fps_generation or not self.fps_running:
            return
        try:
            game_open = any(
                (proc.info.get("name") or "").lower() == game_name.lower()
                for proc in psutil.process_iter(["name"])
            )
        except (psutil.Error, OSError):
            game_open = True  # falha de permissao nao deve encerrar a captura
        if game_open:
            self.fps_game_seen = True
            self.fps_game_missing_checks = 0
        elif self.fps_game_seen:
            self.fps_game_missing_checks += 1
            if self.fps_game_missing_checks >= 3:
                self.stop_fps_counter()
                self.fps_status_var.set(f"Jogo {game_name} fechado; captura parada.")
                return
        self.root.after(1500, lambda: self._monitor_fps_game(generation, game_name))

    def _update_game_fps(self, fps, sample_count, game_name, generation=None):
        if generation is not None and (generation != self.fps_generation or not self.fps_running):
            return
        self.fps_current = fps
        self.fps_samples.append(fps)
        if len(self.fps_samples) > 120:
            del self.fps_samples[:-120]
        self.fps_average = sum(self.fps_samples) / len(self.fps_samples)
        self._refresh_fps_label(game_name)
        self.fps_status_var.set(f"PresentMon a medir {game_name}; média móvel de {sample_count} tempos de frame.")
        self._update_overlay_display(game_name)

    def _finish_fps_capture(self, game_name, code, error, headers_ok, first_row, generation=None):
        if generation is not None and generation != self.fps_generation:
            return
        self.fps_running = False
        self.fps_process = None
        self.fps_btn_text("INICIAR MEDIÇÃO")
        self._refresh_fps_label(game_name)
        if error:
            status = f"PresentMon terminou ({code}): {error[:260]}"
        elif not headers_ok or first_row:
            status = "Sem quadros recebidos. Confirme o nome .exe, o jogo em execução e permissões ETW (Performance Log Users/admin)."
        else:
            status = f"Captura de {game_name} encerrada (código {code})."
        self.fps_status_var.set(status)
        self.log(status, "AVISO" if code else "INFO")

    def fps_btn_text(self, text):
        if getattr(self, "btn_fps", None) is not None:
            self.btn_fps.config(text=text)

    def stop_fps_counter(self):
        if not self.fps_running:
            return
        self.fps_running = False
        self.fps_generation += 1
        process = self.fps_process
        self.fps_process = None
        self.fps_btn_text("INICIAR MEDIÇÃO")
        if process is not None and process.poll() is None:
            try:
                process.terminate()
                process.wait(timeout=2)
            except Exception:
                try:
                    process.kill()
                except Exception:
                    pass
        self.fps_status_var.set("Medição parada.")
        self.log("Captura PresentMon parada.")

    def reset_fps_counter(self):
        self.fps_current = 0.0
        self.fps_average = 0.0
        self.fps_samples = []
        self._refresh_fps_label()
        self._update_overlay_display()

    def _refresh_fps_label(self, game_name=""):
        if getattr(self, "lbl_fps", None) is None:
            return
        if self.fps_current > 0:
            text = f"{self.fps_current:.0f} FPS"
            if self.fps_show_avg and self.fps_samples:
                text += f"  ·  média {self.fps_average:.0f}"
            if game_name:
                text += f"  ·  {game_name}"
        elif self.fps_running:
            text = "Aguardando FPS do jogo..." + (f" · {game_name}" if game_name else "")
        else:
            text = "FPS do jogo: --" + (f" · {game_name}" if game_name else "")
        self.lbl_fps.config(text=text)

    def _update_overlay_display(self, game_name=""):
        if self.fps_overlay_value is None or not self.fps_overlay_value.winfo_exists():
            return
        value = f"{self.fps_current:.0f} FPS" if self.fps_current > 0 else ("Aguardando..." if self.fps_running else "-- FPS")
        if self.fps_show_avg and self.fps_samples:
            value += f"  |  média {self.fps_average:.0f}"
        self.fps_overlay_value.config(text=value)
        if game_name and self.fps_overlay_game is not None and self.fps_overlay_game.winfo_exists():
            self.fps_overlay_game.config(text=game_name)

    def _show_fps_overlay(self, game_name=""):
        if self.fps_overlay is not None:
            try:
                if self.fps_overlay.winfo_exists():
                    self.fps_overlay.deiconify()
                    self.fps_overlay.lift()
                    self.btn_overlay.config(text="OCULTAR OVERLAY")
                    return
            except tk.TclError:
                pass
        overlay = tk.Toplevel(self.root)
        overlay.title("Mago FPS")
        overlay.overrideredirect(True)
        overlay.attributes("-topmost", True)
        overlay.attributes("-alpha", 0.88)
        overlay.configure(bg="#080B10")
        x, y = self.fps_overlay_position
        overlay.geometry(f"240x72+{x}+{y}")
        overlay.bind("<ButtonPress-1>", self._overlay_drag_start)
        overlay.bind("<B1-Motion>", self._overlay_drag_move)
        overlay.bind("<ButtonRelease-1>", self._overlay_drag_end)
        overlay.bind("<Button-3>", lambda _event: self.hide_fps_overlay())
        overlay.protocol("WM_DELETE_WINDOW", self._fps_overlay_closed)
        value = tk.Label(overlay, text="-- FPS", font=("Consolas", 22, "bold"), fg=self.green, bg="#080B10")
        value.pack(anchor="w", padx=12, pady=(8, 0))
        game = tk.Label(overlay, text=game_name, font=("Segoe UI", 8), fg=self.text, bg="#080B10")
        game.pack(anchor="w", padx=13)
        self.fps_overlay = overlay
        self.fps_overlay_value = value
        self.fps_overlay_game = game
        self.fps_overlay_offset = (0, 0)
        self.btn_overlay.config(text="OCULTAR OVERLAY")
        self._update_overlay_display(game_name)
        self._update_overlay_display(game_name)

    def _overlay_drag_start(self, event):
        self.fps_overlay_offset = (event.x, event.y)
        self.fps_overlay_drag_origin = (self.fps_overlay.winfo_x(), self.fps_overlay.winfo_y())

    def _overlay_drag_move(self, event):
        if self.fps_overlay is not None:
            dx = event.x - self.fps_overlay_offset[0]
            dy = event.y - self.fps_overlay_offset[1]
            x = max(0, self.fps_overlay_drag_origin[0] + dx)
            y = max(0, self.fps_overlay_drag_origin[1] + dy)
            self.fps_overlay_position = [x, y]
            self.fps_overlay.geometry(f"+{x}+{y}")

    def _overlay_drag_end(self, _event):
        if self.fps_overlay is not None:
            self.fps_overlay_position = [self.fps_overlay.winfo_x(), self.fps_overlay.winfo_y()]
            self._save_fps_preferences()

    def toggle_fps_overlay(self):
        try:
            visible = self.fps_overlay is not None and self.fps_overlay.winfo_exists() and self.fps_overlay.state() != "withdrawn"
        except tk.TclError:
            visible = False
        if visible:
            self.hide_fps_overlay()
        else:
            selected = os.path.basename(self.fps_game_var.get().strip().strip('"'))
            target = self.fps_targets.get(selected, {})
            self._show_fps_overlay(target.get("name", selected or "Mago FPS"))

    def hide_fps_overlay(self):
        if self.fps_overlay is not None:
            try:
                self.fps_overlay.withdraw()
            except tk.TclError:
                pass
        if getattr(self, "btn_overlay", None) is not None:
            self.btn_overlay.config(text="MOSTRAR OVERLAY")

    def _fps_overlay_closed(self):
        if getattr(self, "btn_overlay", None) is not None:
            self.btn_overlay.config(text="MOSTRAR OVERLAY")
            self.fps_overlay_enabled = False
            if hasattr(self, "fps_overlay_var") and self.fps_overlay_var is not None:
                self.fps_overlay_var.set(False)

    def close_fps_overlay(self):
        overlay = self.fps_overlay
        self.fps_overlay = None
        self.fps_overlay_value = None
        self.fps_overlay_game = None
        if getattr(self, "btn_overlay", None) is not None:
            self.btn_overlay.config(text="MOSTRAR OVERLAY")
        if overlay is not None:
            try:
                overlay.destroy()
            except tk.TclError:
                pass

    def _overlay_show(self, game_name=""):
        self._show_fps_overlay(game_name)

    def build_page_otimizar(self):
        page = tk.Frame(self.content, bg=self.bg)
        self.pages["otimizar"] = page
        tk.Label(page, text="Otimização", font=("Segoe UI", 16, "bold"), fg=self.text, bg=self.bg).pack(anchor="w")

        mode = self.card_frame(page, "PERFIL")
        mode.pack(fill="x", pady=8)
        inner = tk.Frame(mode, bg=self.card)
        inner.pack(fill="x", padx=12, pady=(0, 10))
        tk.Radiobutton(
            inner,
            text="Equilibrado — FPS sem desligar segurança, TDR nem timers de boot",
            variable=self.mode,
            value="balanced",
            font=("Segoe UI", 9),
            fg=self.text,
            bg=self.card,
            activebackground=self.card,
            selectcolor="#0B0E14",
            highlightthickness=0,
            anchor="w",
        ).pack(fill="x")
        tk.Radiobutton(
            inner,
            text="Competitivo — VBS, bcdedit, SysMain e TDR (maior risco, usa backup)",
            variable=self.mode,
            value="competitive",
            font=("Segoe UI", 9),
            fg=self.text,
            bg=self.card,
            activebackground=self.card,
            selectcolor="#0B0E14",
            highlightthickness=0,
            anchor="w",
        ).pack(fill="x")

        mods = self.card_frame(page, "MÓDULOS")
        mods.pack(fill="x", pady=8)
        grid = tk.Frame(mods, bg=self.card)
        grid.pack(fill="x", padx=12, pady=(0, 12))
        grid.columnconfigure(0, weight=1)
        grid.columnconfigure(1, weight=1)
        buttons = [
            ("GPU  ·  HAGS / NVIDIA", self.tweak_gpu),
            ("Jogos  ·  prioridade e Game DVR", self.tweak_windows_gaming),
            ("CPU/GPU  ·  agendamento multimedia", self.tweak_reg),
            ("Energia  ·  Ultimate Performance", self.tweak_power),
            ("Serviços  ·  telemetria", self.tweak_services),
            ("Limpeza  ·  Temp e DNS", self.clean_temp_and_ram),
            ("Rede  ·  TCP/IP latência", self.tweak_network),
            ("Input  ·  rato / kernel", self.tweak_input_lag),
            ("SSD  ·  TRIM e manutenção", self.tweak_ssd),
            ("Apps  ·  fechar em fundo", self.kill_background),
        ]
        for i, (text, command) in enumerate(buttons):
            btn = self.ghost_btn(grid, text, lambda c=command: self.start_module(c), bg="#1B2430")
            btn.grid(row=i // 2, column=i % 2, sticky="ew", padx=4, pady=4)

        actions = tk.Frame(page, bg=self.bg)
        actions.pack(fill="x", pady=6)
        self.ghost_btn(actions, "APLICAR TUDO", self.apply_all, bg=self.red, pady=12).pack(side="left", expand=True, fill="x", padx=(0, 6))
        self.ghost_btn(actions, "TESTE DE STRESSE 15s", self.run_stress_test, bg="#3A2E12", fg=self.gold, pady=12).pack(
            side="left", expand=True, fill="x", padx=6
        )
        self.ghost_btn(actions, "ABRIR LOG", self.open_log_file, bg="#1B2430", pady=12).pack(side="left", expand=True, fill="x", padx=(6, 0))

    def build_page_apps(self):
        page = tk.Frame(self.content, bg=self.bg)
        self.pages["apps"] = page
        tk.Label(page, text="Aplicativos", font=("Segoe UI", 16, "bold"), fg=self.text, bg=self.bg).pack(anchor="w")
        tk.Label(
            page,
            text="Desinstala pelo método oficial. Se falhar, a remoção forçada limpa leftover de registry e pasta (com confirmação).",
            font=("Segoe UI", 9),
            fg=self.dim,
            bg=self.bg,
            wraplength=980,
            justify="left",
        ).pack(anchor="w", pady=(0, 8))

        bar = tk.Frame(page, bg=self.bg)
        bar.pack(fill="x", pady=(0, 8))
        tk.Entry(
            bar,
            textvariable=self.search_var,
            bg="#10161F",
            fg=self.text,
            insertbackground=self.text,
            bd=0,
            highlightthickness=1,
            highlightbackground=self.line,
            font=("Segoe UI", 10),
        ).pack(side="left", fill="x", expand=True, ipady=6)
        self.search_var.trace_add("write", lambda *_: self.filter_apps())
        self.ghost_btn(bar, "Atualizar lista", lambda: threading.Thread(target=self.refresh_apps, daemon=True).start(), bg="#1B2430").pack(
            side="left", padx=6
        )
        self.ghost_btn(bar, "Desinstalar", self.uninstall_selected, bg="#2A1A22").pack(side="left", padx=4)
        self.ghost_btn(bar, "Forçar remoção", self.force_uninstall_selected, bg=self.red).pack(side="left", padx=4)
        self.lbl_apps_status = tk.Label(
            page,
            text="A carregar aplicativos...",
            font=("Segoe UI", 8),
            fg=self.dim,
            bg=self.bg,
            anchor="w",
        )
        self.lbl_apps_status.pack(fill="x", pady=(0, 4))

        cols = ("nome", "versao", "publisher", "tipo")
        wrap = tk.Frame(page, bg=self.bg)
        wrap.pack(fill="both", expand=True)
        self.apps_tree = ttk.Treeview(wrap, columns=cols, show="headings", style="Mago.Treeview")
        self.apps_tree.heading("nome", text="Nome")
        self.apps_tree.heading("versao", text="Versão")
        self.apps_tree.heading("publisher", text="Publisher")
        self.apps_tree.heading("tipo", text="Tipo")
        self.apps_tree.column("nome", width=360)
        self.apps_tree.column("versao", width=110)
        self.apps_tree.column("publisher", width=240)
        self.apps_tree.column("tipo", width=90)
        scroll = ttk.Scrollbar(wrap, orient="vertical", command=self.apps_tree.yview)
        self.apps_tree.configure(yscrollcommand=scroll.set)
        self.apps_tree.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")
        self.apps_tree.bind("<Double-1>", lambda _e: self.uninstall_selected())

    def build_page_backup(self):
        page = tk.Frame(self.content, bg=self.bg)
        self.pages["backup"] = page
        tk.Label(page, text="Backup e restauro", font=("Segoe UI", 16, "bold"), fg=self.text, bg=self.bg).pack(anchor="w")
        tk.Label(
            page,
            text="Guarda o estado atual dos tweaks, exporta para ficheiro e cria ponto de restauro do Windows.",
            font=("Segoe UI", 9),
            fg=self.dim,
            bg=self.bg,
        ).pack(anchor="w", pady=(0, 8))

        info = self.card_frame(page, "ESTADO")
        info.pack(fill="x", pady=6)
        self.lbl_backup = tk.Label(info, text="Sem backup.", font=("Consolas", 9), fg=self.text, bg=self.card, justify="left", anchor="w")
        self.lbl_backup.pack(fill="x", padx=12, pady=(0, 12))

        grid = tk.Frame(page, bg=self.bg)
        grid.pack(fill="x", pady=8)
        for i in range(3):
            grid.columnconfigure(i, weight=1)
        self.ghost_btn(grid, "Guardar backup agora\n(captura o sistema atual)", self.capture_backup_now, bg=self.purple, pady=14).grid(
            row=0, column=0, sticky="ew", padx=4, pady=4
        )
        self.ghost_btn(grid, "Restaurar backup Mago\n(repor tweaks)", self.revert_all, bg="#3A1F6E", pady=14).grid(
            row=0, column=1, sticky="ew", padx=4, pady=4
        )
        self.ghost_btn(grid, "Ponto de restauro Windows", self.make_restore_point, bg="#1F3A2E", pady=14).grid(
            row=0, column=2, sticky="ew", padx=4, pady=4
        )
        self.ghost_btn(grid, "Exportar backup...", self.export_backup, bg="#1B2430", pady=14).grid(row=1, column=0, sticky="ew", padx=4, pady=4)
        self.ghost_btn(grid, "Importar backup...", self.import_backup, bg="#1B2430", pady=14).grid(row=1, column=1, sticky="ew", padx=4, pady=4)
        self.ghost_btn(grid, "Abrir pasta de snapshots", self.open_snapshot_dir, bg="#1B2430", pady=14).grid(
            row=1, column=2, sticky="ew", padx=4, pady=4
        )

        rp = self.card_frame(page, "PONTOS DE RESTAURO DO WINDOWS")
        rp.pack(fill="both", expand=True, pady=6)
        self.rp_tree = ttk.Treeview(rp, columns=("seq", "data", "desc"), show="headings", style="Mago.Treeview", height=6)
        self.rp_tree.heading("seq", text="ID")
        self.rp_tree.heading("data", text="Data")
        self.rp_tree.heading("desc", text="Descrição")
        self.rp_tree.column("seq", width=80)
        self.rp_tree.column("data", width=160)
        self.rp_tree.column("desc", width=420)
        self.rp_tree.pack(fill="both", expand=True, padx=12, pady=(0, 8))
        row = tk.Frame(rp, bg=self.card)
        row.pack(fill="x", padx=12, pady=(0, 12))
        self.ghost_btn(row, "Atualizar lista", self.refresh_restore_points, bg="#1B2430").pack(side="left")
        self.ghost_btn(row, "Restaurar ponto selecionado (reinicia o PC)", self.restore_windows_point, bg=self.red).pack(
            side="left", padx=8
        )

    def build_page_bios(self):
        page = tk.Frame(self.content, bg=self.bg)
        self.pages["bios"] = page
        tk.Label(page, text="BIOS e firmware", font=("Segoe UI", 16, "bold"), fg=self.text, bg=self.bg).pack(anchor="w")
        tk.Label(
            page,
            text="Análise local. A atualização em si é sempre no site do fabricante — piscar BIOS pelo app arriscaria brickar a board.",
            font=("Segoe UI", 9),
            fg=self.dim,
            bg=self.bg,
            wraplength=980,
            justify="left",
        ).pack(anchor="w", pady=(0, 8))

        box = self.card_frame(page, "DETALHES")
        box.pack(fill="both", expand=True, pady=6)
        self.lbl_bios = tk.Label(box, text="A ler...", font=("Consolas", 10), fg=self.text, bg=self.card, justify="left", anchor="nw")
        self.lbl_bios.pack(fill="both", expand=True, padx=12, pady=(0, 12))

        row = tk.Frame(page, bg=self.bg)
        row.pack(fill="x")
        self.ghost_btn(row, "Analisar de novo", lambda: threading.Thread(target=self.load_bios_info, daemon=True).start(), bg="#1B2430", pady=12).pack(
            side="left", expand=True, fill="x", padx=(0, 6)
        )
        self.ghost_btn(row, "Abrir página oficial de BIOS", self.open_bios_update, bg=self.cyan, fg="#041016", pady=12).pack(
            side="left", expand=True, fill="x", padx=6
        )
        self.ghost_btn(row, "Windows Update (firmware)", self.open_windows_update, bg="#1F3A2E", pady=12).pack(
            side="left", expand=True, fill="x", padx=(6, 0)
        )

    def build_page_ferramentas(self):
        page = tk.Frame(self.content, bg=self.bg)
        self.pages["ferramentas"] = page
        tk.Label(page, text="Ferramentas extra", font=("Segoe UI", 16, "bold"), fg=self.text, bg=self.bg).pack(anchor="w")
        grid = tk.Frame(page, bg=self.bg)
        grid.pack(fill="x", pady=10)
        grid.columnconfigure(0, weight=1)
        grid.columnconfigure(1, weight=1)

        def add_tool(r, c, text, command):
            self.ghost_btn(grid, text, command, bg="#1B2430", pady=12).grid(row=r, column=c, sticky="ew", padx=5, pady=5)

        add_tool(0, 0, "Efeitos visuais · melhor desempenho", lambda: self.start_module(self.visual_effects_perf))
        add_tool(0, 1, "Limpar cache de shaders", lambda: self.start_module(self.clean_shader_cache))
        add_tool(1, 0, "Desativar arranque rápido", lambda: self.start_module(self.disable_fast_startup))
        add_tool(1, 1, "Atualizar lista de arranque", self.list_startup)
        add_tool(2, 0, "Desativar arranque selecionado", self.disable_startup_selected)
        add_tool(2, 1, "Reiniciar Explorador", lambda: self.start_module(self.restart_explorer))
        add_tool(3, 0, "Flush DNS", lambda: self.start_module(self.flush_dns))
        add_tool(3, 1, "Gráficos do Windows", lambda: open_uri("ms-settings:display-advancedgraphics"))

        box = self.card_frame(page, "PROGRAMAS NO ARRANQUE")
        box.pack(fill="both", expand=True, pady=8)
        self.startup_tree = ttk.Treeview(box, columns=("nome", "hive", "comando"), show="headings", style="Mago.Treeview", height=8)
        self.startup_tree.heading("nome", text="Nome")
        self.startup_tree.heading("hive", text="Local")
        self.startup_tree.heading("comando", text="Comando")
        self.startup_tree.column("nome", width=180)
        self.startup_tree.column("hive", width=70)
        self.startup_tree.column("comando", width=520)
        self.startup_tree.pack(fill="both", expand=True, padx=12, pady=(0, 4))
        self.lbl_startup_status = tk.Label(
            box,
            text="A lista é carregada ao abrir esta página.",
            font=("Segoe UI", 8),
            fg=self.dim,
            bg=self.card,
            anchor="w",
        )
        self.lbl_startup_status.pack(fill="x", padx=12, pady=(0, 10))

    def build_page_rede(self):
        page = tk.Frame(self.content, bg=self.bg)
        self.pages["rede"] = page
        tk.Label(page, text="Rede e velocidade", font=("Segoe UI", 16, "bold"), fg=self.text, bg=self.bg).pack(anchor="w")
        tk.Label(
            page,
            text=(
                "Ping com jitter e perda, download e upload reais e o DNS que responde mais rápido. "
                "Cada teste completo transfere até ~100 MB de download e ~24 MB de upload — "
                "evita-o em ligações com dados limitados."
            ),
            font=("Segoe UI", 9),
            fg=self.dim,
            bg=self.bg,
            wraplength=980,
            justify="left",
        ).pack(anchor="w", pady=(0, 8))

        info = self.card_frame(page, "LIGAÇÃO ATUAL")
        info.pack(fill="x", pady=6)
        self.lbl_net_info = tk.Label(
            info,
            text="A analisar a ligação...",
            font=("Consolas", 9),
            fg=self.text,
            bg=self.card,
            justify="left",
            anchor="w",
        )
        self.lbl_net_info.pack(fill="x", padx=12, pady=(0, 6))
        info_row = tk.Frame(info, bg=self.card)
        info_row.pack(fill="x", padx=12, pady=(0, 10))
        self.ghost_btn(info_row, "Analisar ligação", self.refresh_net_info, bg="#1B2430").pack(side="left")
        self.ghost_btn(
            info_row,
            "Configurações de rede do Windows",
            lambda: open_uri("ms-settings:network-status"),
            bg="#1B2430",
        ).pack(side="left", padx=8)

        stats = tk.Frame(page, bg=self.bg)
        stats.pack(fill="x", pady=4)
        self.net_stats = {}
        for column, (key, caption, color) in enumerate(
            [
                ("ping", "PING MÉDIO", self.green),
                ("jitter", "JITTER", self.cyan),
                ("loss", "PERDA", self.red),
                ("down", "DOWNLOAD", self.gold),
                ("up", "UPLOAD", self.purple),
            ]
        ):
            stats.columnconfigure(column, weight=1)
            box = tk.Frame(stats, bg=self.card, highlightbackground=self.line, highlightthickness=1)
            box.grid(row=0, column=column, sticky="nsew", padx=4)
            tk.Label(box, text=caption, font=("Segoe UI", 8, "bold"), fg=self.dim, bg=self.card, anchor="w").pack(
                fill="x", padx=10, pady=(8, 0)
            )
            value = tk.Label(box, text="--", font=("Consolas", 15, "bold"), fg=color, bg=self.card, anchor="w")
            value.pack(fill="x", padx=10, pady=(0, 9))
            self.net_stats[key] = value

        test = self.card_frame(page, "TESTE")
        test.pack(fill="x", pady=6)
        host_row = tk.Frame(test, bg=self.card)
        host_row.pack(fill="x", padx=12, pady=3)
        tk.Label(host_row, text="Servidor:", font=("Segoe UI", 9), fg=self.text, bg=self.card).pack(side="left")
        tk.Entry(
            host_row,
            textvariable=self.net_host_var,
            bg="#10161F",
            fg=self.text,
            insertbackground=self.text,
            bd=0,
            highlightthickness=1,
            highlightbackground=self.line,
            font=("Segoe UI", 9),
        ).pack(side="left", padx=8, fill="x", expand=True, ipady=5)
        for host in ("1.1.1.1", "8.8.8.8", "google.com"):
            self.ghost_btn(host_row, host, lambda h=host: self.net_host_var.set(h), bg="#1B2430").pack(
                side="left", padx=(6, 0)
            )

        actions = tk.Frame(test, bg=self.card)
        actions.pack(fill="x", padx=12, pady=(8, 10))
        self.ghost_btn(
            actions,
            "TESTE COMPLETO\n(rede + ping + velocidade)",
            self.run_network_test,
            bg="#2A2410",
            fg=self.gold,
            pady=10,
        ).pack(side="left", expand=True, fill="x", padx=(0, 6))
        self.ghost_btn(actions, "Só ping\n(latência e perda)", self.run_ping_test, bg="#1B2430", pady=10).pack(
            side="left", expand=True, fill="x", padx=6
        )
        self.ghost_btn(actions, "Só velocidade\n(download e upload)", self.run_speed_test, bg="#1B2430", pady=10).pack(
            side="left", expand=True, fill="x", padx=(6, 0)
        )

        dns = self.card_frame(page, "DNS — MELHOR RESOLVEDOR")
        dns.pack(fill="x", pady=6)
        self.lbl_dns_current = tk.Label(
            dns,
            text="DNS atual: a ler...",
            font=("Consolas", 9),
            fg=self.text,
            bg=self.card,
            anchor="w",
            justify="left",
            wraplength=980,
        )
        self.lbl_dns_current.pack(fill="x", padx=12)
        tk.Label(
            dns,
            textvariable=self.dns_status_var,
            font=("Consolas", 9),
            fg=self.text,
            bg=self.card,
            anchor="w",
            justify="left",
            wraplength=980,
        ).pack(fill="x", padx=12, pady=(4, 0))
        dns_row = tk.Frame(dns, bg=self.card)
        dns_row.pack(fill="x", padx=12, pady=(8, 10))
        self.ghost_btn(
            dns_row,
            "Analisar DNS\n(latência de cada resolvedor)",
            self.run_dns_benchmark,
            bg="#132A24",
            fg=self.green,
            pady=8,
        ).pack(side="left")
        self.dns_combo = ttk.Combobox(
            dns_row,
            textvariable=self.dns_choice_var,
            values=dns_choices(),
            state="readonly",
            style="Mago.Treeview",
            width=34,
        )
        self.dns_combo.pack(side="left", padx=8, ipady=3)
        self.ghost_btn(dns_row, "Aplicar DNS", self.apply_dns, bg="#1B2430").pack(side="left")
        tk.Label(
            dns,
            text="Se ficares sem internet depois de trocar, escolhe Automático (DHCP) e aplica outra vez.",
            font=("Segoe UI", 8),
            fg=self.dim,
            bg=self.card,
            anchor="w",
            justify="left",
            wraplength=980,
        ).pack(fill="x", padx=12, pady=(0, 8))

        tk.Label(
            page,
            textvariable=self.net_status_var,
            font=("Segoe UI", 9),
            fg=self.dim,
            bg=self.bg,
            anchor="w",
            justify="left",
            wraplength=980,
        ).pack(fill="x", pady=(8, 0))
        self.lbl_net_diag = tk.Label(
            page,
            text="",
            font=("Segoe UI", 10, "bold"),
            fg=self.gold,
            bg=self.bg,
            anchor="w",
            justify="left",
            wraplength=980,
        )
        self.lbl_net_diag.pack(fill="x", pady=(4, 0))

    NET_PING_COUNT = 5
    NET_DOWNLOAD_BYTES = 25_000_000
    NET_DOWNLOAD_SECONDS = 15.0
    NET_UPLOAD_BYTES = 4_000_000

    def refresh_net_info(self):
        if getattr(self, "net_info_loading", False):
            return
        self.net_info_loading = True
        # Chamada na thread principal, por isso escreve direto: passar por
        # _net_info_apply libertaria o guard antes do worker terminar.
        self.lbl_net_info.config(text="A analisar a ligação...")
        threading.Thread(target=self._net_info_worker, daemon=True).start()

    def _net_info_worker(self):
        route = {}
        try:
            route = self.default_route()
            text = self.network_report(route)
        except Exception as exc:
            text = f"Não foi possível analisar a ligação: {exc}"
        self._net_info_apply(text)
        self._dns_update_current(route)

    def _net_info_apply(self, text):
        def _apply():
            self.lbl_net_info.config(text=text)
            self.net_info_loading = False

        self.ui(_apply)

    def _net_status(self, text):
        self.ui(lambda: self.net_status_var.set(text))

    def _net_set(self, key, value):
        label = self.net_stats.get(key)
        if label is None:
            return
        self.ui(lambda: label.config(text=value))

    def _net_update_diagnosis(self):
        text = diagnose_network(
            ping=self.net_last.get("ping"),
            gateway=self.net_last.get("gateway"),
            down_bps=self.net_last.get("download"),
            up_bps=self.net_last.get("upload"),
        )
        self.ui(lambda: self.lbl_net_diag.config(text=text))

    def default_route(self):
        """Gateway e DNS do adaptador com rota padrão.

        Lido por CIM (nomes de propriedade em inglês), para não depender do
        idioma do Windows como a leitura do `ipconfig` faria.
        """
        ok, data, _out = ps_json(
            "Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' | "
            "Where-Object { $_.DefaultIPGateway } | Select-Object -First 1 "
            "Description, @{n='Gateway';e={$_.DefaultIPGateway -join ', '}}, "
            "@{n='DNS';e={$_.DNSServerSearchOrder -join ', '}}",
            timeout=45,
        )
        if not ok or not data:
            return {}
        row = data[0] if isinstance(data, list) else data
        if not isinstance(row, dict):
            return {}
        return {
            "description": str(row.get("Description") or "").strip(),
            "gateway": str(row.get("Gateway") or "").strip(),
            "dns": str(row.get("DNS") or "").strip(),
        }

    def network_report(self, route=None):
        """Adaptadores ativos, IP, gateway, DNS e velocidade de link."""
        try:
            stats = psutil.net_if_stats()
            addrs = psutil.net_if_addrs()
        except Exception as exc:
            return f"Não foi possível ler os adaptadores: {exc}"
        lines = []
        active = []
        for name, stat in sorted(stats.items()):
            if not stat.isup or name.lower().startswith("loopback"):
                continue
            ipv4 = ""
            for item in addrs.get(name, []):
                if item.family == socket.AF_INET and "." in (item.address or ""):
                    ipv4 = item.address
                    break
            if not ipv4:
                continue
            low = name.lower()
            medium = "Wi-Fi" if any(key in low for key in ("wi-fi", "wifi", "wlan", "wireless")) else "cabo"
            # psutil já devolve a velocidade em Mbps. Muitos drives reportam 1
            # (desconhecido), por isso só mostramos a partir de 10 Mbps.
            if stat.speed >= 1000:
                link = f"link {stat.speed / 1000:g} Gbps"
            elif stat.speed >= 10:
                link = f"link {stat.speed:.0f} Mbps"
            else:
                link = "link não reportado"
            active.append(f"  · {name} ({medium}) — IPv4 {ipv4} — {link}")
        if active:
            lines.append("Adaptadores ativos:")
            lines.extend(active)
        else:
            lines.append("Nenhum adaptador ativo com IPv4 — o PC parece estar sem rede.")
        route = route if route is not None else self.default_route()
        if route.get("gateway") or route.get("dns"):
            adapter = f"{route['description']} — " if route.get("description") else ""
            lines.append(f"{adapter}Gateway: {route.get('gateway') or '—'}  ·  DNS: {route.get('dns') or '—'}")
        else:
            lines.append("Gateway/DNS: não disponível (consulta ao Windows falhou).")
        return "\n".join(lines)

    def ping_host(self, host, count=None):
        """Ping ICMP por `ping.exe`; devolve resumo com perda e jitter."""
        count = count or self.NET_PING_COUNT
        ok, out = run(["ping", "-n", str(count), "-w", "1500", host], timeout=45)
        summary = summarize_latency(parse_ping_times(out), count)
        summary["host"] = host
        summary["ok"] = bool(summary["received"])
        summary["output"] = out
        return summary

    def run_ping_test(self):
        # O texto do campo é lido aqui (thread principal): o Tk não permite
        # ler variáveis dele a partir de uma thread de trabalho.
        host = (self.net_host_var.get() or "").strip()
        self.start_module(lambda: self._net_ping_task(host=host))

    def run_speed_test(self):
        self.start_module(self._net_speed_task)

    def run_network_test(self):
        host = (self.net_host_var.get() or "").strip()
        self.start_module(lambda: self._net_full_task(host))

    def _net_ping_task(self, route=None, host=None):
        host = (host or "").strip() or PING_HOST_DEFAULT
        route = route if route is not None else self.default_route()
        self._net_status(f"A medir latência para {host}...")
        summary = self.ping_host(host)
        if summary["ok"]:
            self._net_set("ping", f"{summary['avg']:.0f} ms")
            self._net_set("jitter", f"{summary['jitter']:.0f} ms")
            self._net_set("loss", f"{summary['loss']:.0f}%")
            self.log(
                f"Rede · {host}: min {summary['min']} / média {summary['avg']} / máx {summary['max']} ms"
                f" · jitter {summary['jitter']} ms · perda {summary['loss']}% ({summary['received']}/{summary['sent']})"
            )
        else:
            self._net_set("ping", "sem resposta")
            self._net_set("jitter", "--")
            self._net_set("loss", "100%")
            self.log(f"Rede · {host} não respondeu ao ping (firewall, DNS ou sem internet).", "AVISO")
        self.net_last["ping"] = summary
        gateway = (route.get("gateway") or "").split(",")[0].strip()
        if gateway:
            gate = self.ping_host(gateway, 3)
            self.net_last["gateway"] = gate
            if gate["ok"]:
                self.log(
                    f"Rede · router {gateway}: média {gate['avg']} ms · jitter {gate['jitter']} ms"
                    f" · perda {gate['loss']}%"
                )
            else:
                self.log(f"Rede · router {gateway} não respondeu ao ping.", "AVISO")
        self._net_status(f"Ping concluído para {host}.")
        self._net_update_diagnosis()

    def _net_speed_task(self):
        down = None
        up = None
        self._net_status("A medir download (janela de ~2 s em blocos)...")
        try:
            total, seconds = measure_download(self.NET_DOWNLOAD_BYTES, self.NET_DOWNLOAD_SECONDS)
            down = bits_per_second(total, seconds)
            self._net_set("down", format_mbps(down))
            self.log(f"Rede · download: {format_mbps(down)} ({total / 1e6:.1f} MB em {seconds:.1f}s)")
        except Exception as exc:
            self._net_set("down", "erro")
            self.log(f"Rede · download falhou: {exc}", "ERRO")
        self._net_status("A medir upload (janela de ~2 s em blocos)...")
        try:
            total, seconds = measure_upload(self.NET_UPLOAD_BYTES)
            up = bits_per_second(total, seconds)
            self._net_set("up", format_mbps(up))
            self.log(f"Rede · upload: {format_mbps(up)} ({total / 1e6:.1f} MB em {seconds:.1f}s)")
        except Exception as exc:
            self._net_set("up", "erro")
            self.log(f"Rede · upload falhou: {exc}", "ERRO")
        self.net_last["download"] = down
        self.net_last["upload"] = up
        self._net_status("Velocidade medida." if (down or up) else "Não foi possível medir a velocidade.")
        self._net_update_diagnosis()

    def _net_full_task(self, host=None):
        self._net_status("A analisar a ligação...")
        route = self.default_route()
        self._net_info_apply(self.network_report(route))
        self._dns_update_current(route)
        self._net_ping_task(route, host)
        self._net_speed_task()
        self._dns_benchmark_task(route)
        self._net_status("Teste completo concluído — vê o diagnóstico abaixo e o log na consola.")

    def default_interface(self):
        """Nome do adaptador com rota padrão — é esse o nome que o netsh usa."""
        ok, data, _out = ps_json(
            "Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway } | Select-Object -First 1 "
            "InterfaceAlias, InterfaceIndex, "
            "@{n='Gateway';e={ (@($_.IPv4DefaultGateway) | Select-Object -First 1).NextHop }}, "
            "@{n='DNS';e={$_.DNSServer.ServerAddresses -join ', '}}",
            timeout=45,
        )
        row = data[0] if isinstance(data, list) and data else data
        if ok and isinstance(row, dict) and row.get("InterfaceAlias"):
            return {
                "alias": str(row.get("InterfaceAlias") or "").strip(),
                "index": row.get("InterfaceIndex"),
                "gateway": str(row.get("Gateway") or "").strip(),
                "dns": str(row.get("DNS") or "").strip(),
            }
        # Windows antigo: traduz a descrição do CIM para o nome da ligação.
        route = self.default_route()
        description = (route.get("description") or "").replace("'", "''")
        if description:
            ok, data, _out = ps_json(
                "Get-CimInstance Win32_NetworkAdapter | Where-Object { $_.Description -eq '"
                + description
                + "' } | Select-Object -First 1 NetConnectionID, InterfaceIndex",
                timeout=45,
            )
            row = data[0] if isinstance(data, list) and data else data
            if ok and isinstance(row, dict) and row.get("NetConnectionID"):
                return {
                    "alias": str(row.get("NetConnectionID") or "").strip(),
                    "index": row.get("InterfaceIndex"),
                    "gateway": route.get("gateway") or "",
                    "dns": route.get("dns") or "",
                }
        return {}

    def _dns_status(self, text):
        self.ui(lambda: self.dns_status_var.set(text))

    def _dns_update_current(self, route=None):
        route = route if route is not None else self.default_route()
        servers = split_dns_servers(route.get("dns"))
        text = "DNS atual: " + (", ".join(servers) if servers else "não disponível")
        if route.get("description"):
            text += f"   ·   {route['description']}"
        self.ui(lambda: self.lbl_dns_current.config(text=text))

    def run_dns_benchmark(self):
        self.start_module(self._dns_benchmark_task)

    def _dns_benchmark_task(self, route=None):
        route = route if route is not None else self.default_route()
        servers = split_dns_servers(route.get("dns"))
        current = servers[0] if servers else ""
        self._dns_update_current(route)
        self._dns_status("A medir a latência de cada resolvedor DNS...")
        try:
            results = benchmark_dns(dns_candidates(current))
        except Exception as exc:
            self.log(f"DNS · falha ao testar: {exc}", "ERRO")
            self._dns_status(f"Falha ao testar DNS: {exc}")
            return
        self.dns_results = results
        self.dns_best = results[0] if results and results[0].get("ok") else None
        self._dns_status(format_dns_ranking(results) + "\n" + dns_recommendation(results))
        self.ui(lambda: self.dns_combo.config(values=dns_choices(results)))
        resumo = "; ".join(
            f"{item['name']}: " + (f"{item['median']:.1f} ms" if item.get("ok") else "sem resposta")
            for item in results
        )
        self.log(f"DNS · {resumo}")

    def apply_dns(self):
        label = (self.dns_choice_var.get() or "").strip()
        if not label:
            messagebox.showinfo("DNS", "Escolhe um resolvedor na lista (ou Automático para voltar a DHCP).")
            return
        provider = dns_choice_provider(label, self.dns_results or None)
        if not label.startswith("Automático") and provider is None:
            messagebox.showwarning("DNS", "Escolhe um resolvedor da lista antes de aplicar.")
            return
        if provider and self.dns_results:
            tested = next(
                (item for item in self.dns_results if item.get("primary") == provider.get("primary")), None
            )
            if tested is not None and not tested.get("ok"):
                if not messagebox.askyesno(
                    "DNS",
                    f"{provider['name']} não respondeu ao teste DNS nesta rede.\n\n"
                    "Aplicar mesmo assim pode deixar o PC sem resolução de nomes. Continuar?",
                ):
                    return
        if not check_admin():
            messagebox.showwarning(
                "DNS",
                "Trocar o DNS precisa de administrador.\n\nFecha o Mago e abre de novo como administrador.",
            )
            return
        self.start_module(lambda: self._dns_apply_task(provider, label))

    def _dns_apply_task(self, provider, label):
        interface = self.default_interface()
        alias = interface.get("alias") or ""
        if not alias:
            self.log("DNS · não encontrei o adaptador de rede com rota padrão.", "ERRO")
            self._dns_status("Não encontrei o adaptador de rede ativo para trocar o DNS.")
            return
        target = (provider or {}).get("primary") or ""
        alvo = label or DNS_AUTO_LABEL
        self._dns_status(f"A aplicar {alvo} em {alias}...")
        try:
            commands = dns_change_commands(alias, target, (provider or {}).get("secondary") or "")
        except ValueError as exc:
            self._dns_status(f"Adaptador inválido: {exc}")
            return
        for args in commands:
            ok, out = run(args, timeout=45)
            if not ok:
                self.log(f"DNS · falhou: {' '.join(args)} → {out[:200]}", "ERRO")
                self._dns_status(
                    "Não foi possível trocar o DNS. Confirma que o Mago está a correr como administrador."
                )
                return
        run(["ipconfig", "/flushdns"], timeout=30)
        self.log(f"DNS · {alvo} aplicado em {alias}; cache DNS limpo.")
        self._dns_status(f"DNS aplicado: {alvo} em {alias}. O cache DNS foi limpo.")
        route = self.default_route()
        self._net_info_apply(self.network_report(route))
        self._dns_update_current(route)

    def tick_meters(self):
        try:
            cpu = psutil.cpu_percent(interval=None)
            ram = psutil.virtual_memory().percent
            disk = psutil.disk_usage("C:\\").percent
            self.lbl_cpu.config(text=f"CPU {cpu:.0f}%")
            self.lbl_ram.config(text=f"RAM {ram:.0f}%")
            self.lbl_disk.config(text=f"C: {disk:.0f}%")
        except Exception:
            pass
        self.root.after(1500, self.tick_meters)

    def start_module(self, fn):
        if not self.claim_busy():
            return
        threading.Thread(target=lambda: self._run_module(fn), daemon=True).start()

    def _run_module(self, fn):
        self.set_busy(True)
        try:
            fn()
            self.backup.save()
        except Exception as exc:
            self.log(f"Falha no módulo: {exc}", "ERRO")
        finally:
            self.set_busy(False)

    def open_log_file(self):
        if os.path.exists(LOG_FILE):
            os.startfile(LOG_FILE)
        else:
            messagebox.showinfo("Log", "Ainda não há log.")

    def is_competitive(self):
        return self.mode.get() == "competitive"

    def nvidia_present(self):
        return "nvidia" in (self.gpu_name or "").lower()

    def has_hdd(self):
        return any(t.upper() == "HDD" for t in self.disk_types)

    def snapshot_reg(self, key, name, overwrite=False):
        ok, out = run(["reg", "query", key, "/v", name])
        parsed = parse_reg_query(out, name) if ok else None
        if parsed:
            snapshot = {"existed": True, "key": key, "name": name, "type": parsed["type"], "data": parsed["data"]}
        else:
            snapshot = {"existed": False, "key": key, "name": name, "type": None, "data": None}
        self.backup.remember_reg(key, name, snapshot, overwrite=overwrite)
        return snapshot

    def set_reg(self, key, name, reg_type, data):
        self.snapshot_reg(key, name)
        ok, out = run(["reg", "add", key, "/v", name, "/t", reg_type, "/d", str(data), "/f"])
        return self.report(ok, f"Registry OK: {name}={data}", f"Registry falhou: {name}", out)

    def snapshot_service(self, name, overwrite=False):
        ok, out = run(["sc", "qc", name])
        start = "unknown"
        if ok:
            for line in out.splitlines():
                if "START_TYPE" in line.upper():
                    match = re.search(r":\s*([0-9])\b", line) or re.search(r"\b([0-9])\b", line)
                    if match:
                        start = {"2": "auto", "3": "demand", "4": "disabled"}.get(match.group(1), "demand")
        running = False
        ok_q, out_q = run(["sc", "query", name])
        if ok_q and "RUNNING" in out_q.upper():
            running = True
        snap = {"name": name, "start": start, "running": running}
        self.backup.remember_service(name, snap, overwrite=overwrite)
        return snap

    def set_service_disabled(self, name):
        self.snapshot_service(name)
        ok_cfg, out_cfg = run(["sc", "config", name, "start=", "disabled"])
        ok_stop, out_stop = run(["sc", "stop", name])
        return self.report(ok_cfg, f"Serviço {name} desativado", f"Não foi possível desativar {name}", f"{out_cfg} | {out_stop}")

    def snapshot_bcd(self, name, overwrite=False):
        ok, out = run(["bcdedit", "/enum", "{current}"])
        value = None
        if ok:
            for line in out.splitlines():
                stripped = line.strip()
                if stripped.lower().startswith(name.lower()):
                    parts = stripped.split()
                    if len(parts) >= 2:
                        value = parts[-1]
        snap = {"existed": value is not None, "value": value}
        self.backup.remember_bcd(name, snap, overwrite=overwrite)
        return snap

    def current_power_guid(self):
        ok, out = run(["powercfg", "/getactivescheme"])
        if not ok:
            return None
        for token in out.replace("(", " ").replace(")", " ").split():
            if len(token) == 36 and token.count("-") == 4:
                return token
        return None

    def create_restore_point(self):
        self.log("A tentar criar ponto de restauro do Windows...")
        script = (
            "try { Checkpoint-Computer -Description 'MagoDaPerformance_v6' -RestorePointType 'MODIFY_SETTINGS'; 'OK' } "
            "catch { $_.Exception.Message }"
        )
        ok, out = run(["powershell", "-NoProfile", "-Command", script], timeout=90)
        if ok and "OK" in out:
            self.log("Ponto de restauro criado.")
            return True
        self.log("Ponto de restauro indisponível (limite de 24h ou Proteção do sistema desligada).", "AVISO")
        if out:
            self.log(out, "AVISO")
        return False

    def detect_hardware_auto(self):
        self.log("A identificar hardware...")
        os_info = f"{platform.system()} {platform.release()} ({platform.version()})"
        cpu_name = platform.processor()
        ok, res = run(["powershell", "-NoProfile", "-Command", "Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name"])
        if ok and res:
            cpu_name = res.splitlines()[0].strip()
        cores = psutil.cpu_count(logical=False)
        threads = psutil.cpu_count(logical=True)
        total_ram = round(psutil.virtual_memory().total / (1024 ** 3), 2)
        gpu_str = "Não identificada"
        ok, gpu_names = run(["powershell", "-NoProfile", "-Command", "Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name"])
        if ok and gpu_names:
            gpu_str = ", ".join(g.strip() for g in gpu_names.splitlines() if g.strip())
        self.gpu_name = gpu_str
        disk_str = "Desconhecido"
        ok, disks = run(
            ["powershell", "-NoProfile", "-Command", "Get-PhysicalDisk | ForEach-Object { $_.FriendlyName + ' [' + $_.MediaType + '/' + $_.HealthStatus + ']' }"]
        )
        if ok and disks:
            lines = [d.strip() for d in disks.splitlines() if d.strip()]
            disk_str = ", ".join(lines)
            self.disk_types = []
            for line in lines:
                if "[" in line and "]" in line:
                    inside = line[line.rfind("[") + 1 : line.rfind("]")]
                    self.disk_types.append(inside.split("/")[0])
        self.hw_text = (
            f"SO: {os_info}\n"
            f"CPU: {cpu_name}\n"
            f"Núcleos: {cores}C / {threads}T\n"
            f"RAM: {total_ram} GB\n"
            f"GPU: {gpu_str}\n"
            f"Discos: {disk_str}"
        )
        self.ui(lambda: self.lbl_hw_info.config(text=self.hw_text))
        self.log("Hardware detetado.")

    def load_bios_info(self):
        self.log("A analisar BIOS/firmware...")
        cmd = (
            "$bios=Get-CimInstance Win32_BIOS; $board=Get-CimInstance Win32_BaseBoard; "
            "$cs=Get-CimInstance Win32_ComputerSystem; $prod=Get-CimInstance Win32_ComputerSystemProduct; "
            "$pe=(Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control' -ErrorAction SilentlyContinue).PEFirmwareType; "
            "$cpu=Get-CimInstance Win32_Processor | Select-Object -First 1; "
            "[pscustomobject]@{Vendor=$bios.Manufacturer; Version=$bios.SMBIOSBIOSVersion; Date=$bios.ReleaseDate; "
            "Serial=$bios.SerialNumber; Board=$board.Product; BoardMaker=$board.Manufacturer; Model=$cs.Model; "
            "Maker=$cs.Manufacturer; SKU=$prod.Name; FirmwareType=$pe; Virt=$cpu.VirtualizationFirmwareEnabled; "
            "Hypervisor=$cs.HypervisorPresent}"
        )
        ok, rows, err = ps_json(cmd, timeout=60)
        info = rows[0] if rows else {}
        sb_ok, sb_out = run(["powershell", "-NoProfile", "-Command", "try { if (Confirm-SecureBootUEFI) {'On'} else {'Off'} } catch { 'N/D' }"])
        tpm_ok, tpm_rows, _ = ps_json(
            "try { Get-Tpm | Select-Object TpmPresent,TpmReady,ManufacturerVersion } catch { [pscustomobject]@{TpmPresent=$false} }",
            timeout=40,
        )
        tpm = tpm_rows[0] if tpm_rows else {}
        info["SecureBoot"] = (sb_out or "N/D").splitlines()[-1] if sb_ok or sb_out else "N/D"
        info["TpmPresent"] = tpm.get("TpmPresent")
        info["TpmReady"] = tpm.get("TpmReady")
        self.bios_info = info
        fw = {1: "Legacy BIOS", 2: "UEFI"}.get(info.get("FirmwareType"), str(info.get("FirmwareType") or "?"))
        date = str(info.get("Date") or "")
        age_note = ""
        match = re.search(r"(20\d{2})", date)
        if match:
            year = int(match.group(1))
            if datetime.now().year - year >= 2:
                age_note = f"\nNota: firmware de {year} — vale a pena verificar se o fabricante tem versão mais recente."
        text = (
            f"Fabricante: {info.get('Maker') or info.get('Vendor')}\n"
            f"Modelo: {info.get('Model')}\n"
            f"Motherboard: {info.get('BoardMaker')} {info.get('Board')}\n"
            f"BIOS/UEFI: {info.get('Vendor')}  {info.get('Version')}\n"
            f"Data: {date}\n"
            f"Nº de série / service tag: {info.get('Serial')}\n"
            f"Firmware: {fw}\n"
            f"Secure Boot: {info.get('SecureBoot')}\n"
            f"TPM presente: {info.get('TpmPresent')}   pronto: {info.get('TpmReady')}\n"
            f"Virtualização na firmware: {info.get('Virt')}\n"
            f"Hypervisor ativo: {info.get('Hypervisor')}"
            f"{age_note}"
        )
        dash = f"{info.get('Vendor')} {info.get('Version')}\n{fw} · Secure Boot {info.get('SecureBoot')}\nBoard: {info.get('Board')}"
        self.ui(lambda: (self.lbl_bios.config(text=text), self.lbl_bios_dash.config(text=dash)))
        self.log("Análise de BIOS concluída.")
        if err and not rows:
            self.log(err, "AVISO")

    def open_bios_update(self):
        info = self.bios_info or {}
        url = vendor_support_url(info.get("Maker") or info.get("Vendor") or "", info.get("Serial"), info.get("Model"), info.get("Board"))
        self.log(f"A abrir suporte do fabricante: {url}")
        webbrowser.open(url)

    def open_windows_update(self):
        self.log("A abrir Windows Update (alguns firmware chegam por aqui).")
        open_uri("ms-settings:windowsupdate")

    def _stress_worker(self, stop_event):
        x = 0
        while not stop_event.is_set():
            x += 1
            _ = x * x * x

    def run_stress_test(self):
        if not self.claim_busy("Teste de stresse..."):
            return
        threading.Thread(target=self._execute_stress_test, daemon=True).start()

    def _execute_stress_test(self):
        self.set_busy(True, "Teste de stresse...")
        try:
            self.log("Teste de 15s (CPU sintético, não mede GPU)...")
            stop_event = threading.Event()
            workers = []
            for _ in range(psutil.cpu_count(logical=True) or 4):
                worker = threading.Thread(target=self._stress_worker, args=(stop_event,), daemon=True)
                worker.start()
                workers.append(worker)
            cpu_usages, ram_usages = [], []
            io_start = psutil.disk_io_counters()
            psutil.cpu_percent(interval=None)
            for _ in range(30):
                time.sleep(0.5)
                cpu_usages.append(psutil.cpu_percent(interval=None))
                ram_usages.append(psutil.virtual_memory().percent)
            io_end = psutil.disk_io_counters()
            stop_event.set()
            for worker in workers:
                worker.join(timeout=1)
            avg_cpu = sum(cpu_usages) / len(cpu_usages)
            avg_ram = sum(ram_usages) / len(ram_usages)
            read_mb = write_mb = 0
            if io_start and io_end:
                read_mb = max(0, io_end.read_bytes - io_start.read_bytes) / (1024 ** 2)
                write_mb = max(0, io_end.write_bytes - io_start.write_bytes) / (1024 ** 2)
            self.log(f"CPU {avg_cpu:.1f}% | RAM {avg_ram:.1f}% | Disco {read_mb:.1f}/{write_mb:.1f} MB")
        finally:
            self.set_busy(False)

    def tweak_gpu(self):
        self.log("GPU: HAGS...")
        self.set_reg(r"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "REG_DWORD", 2)
        if self.is_competitive():
            self.log("Competitivo: TdrLevel=0.", "AVISO")
            self.set_reg(r"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrLevel", "REG_DWORD", 0)
        if self.nvidia_present():
            self.set_reg(r"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak", "DisplayPowerSaving", "REG_DWORD", 0)
        else:
            self.log("NVIDIA não detetada.")

    def tweak_windows_gaming(self):
        self.set_reg(r"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "REG_DWORD", 26)
        self.set_reg(r"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", "REG_DWORD", 1)
        self.set_reg(r"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", "REG_DWORD", 1)
        if self.is_competitive():
            self.set_reg(r"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", "REG_DWORD", 0)
            self.set_reg(
                r"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                "Enabled",
                "REG_DWORD",
                0,
            )
            ok, out = run(["powershell", "-NoProfile", "-Command", "Disable-MMAgent -MemoryCompression"])
            self.report(ok, "Compressão de memória off", "Falha MMAgent", out)

    def tweak_reg(self):
        self.set_reg(r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "REG_DWORD", 0)
        games = r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games"
        self.set_reg(games, "GPU Priority", "REG_DWORD", 8)
        self.set_reg(games, "Priority", "REG_DWORD", 6)
        self.set_reg(games, "Scheduling Category", "REG_SZ", "High")
        self.set_reg(r"HKCU\System\GameConfigStore", "GameDVR_Enabled", "REG_DWORD", 0)
        self.set_reg(r"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "REG_DWORD", 0)

    def tweak_power(self):
        self.backup.remember_power(self.current_power_guid())
        run(["powercfg", "-duplicatescheme", self.ULTIMATE_GUID])
        ok, out = run(["powercfg", "-setactive", self.ULTIMATE_GUID])
        self.report(ok, "Ultimate Performance ativo", "Falha no plano de energia", out)

    def tweak_services(self):
        targets = ["DiagTrack", "dmwappushservice", "MapsBroker", "lfsvc"]
        if self.is_competitive() and not self.has_hdd():
            targets += ["SysMain", "WSearch"]
        elif self.is_competitive() and self.has_hdd():
            self.log("HDD detetado: SysMain/WSearch mantidos.", "AVISO")
        for svc in targets:
            self.set_service_disabled(svc)

    def clean_temp_and_ram(self):
        windir = os.environ.get("WINDIR", r"C:\Windows")
        folders = [
            os.path.join(windir, "Temp"),
            os.environ.get("TEMP"),
            os.path.join(os.environ.get("LOCALAPPDATA", ""), "Temp"),
        ]
        seen = set()
        for folder in folders:
            if not folder or folder in seen or not os.path.isdir(folder):
                continue
            seen.add(folder)
            run(["cmd", "/c", f'del /f /q /s "{folder}\\*.*"'])
            self.log(f"Limpeza pedida: {folder}")
        run(["ipconfig", "/flushdns"])
        self.log("Temp + DNS: concluído.")

    def tweak_network(self):
        self.set_reg(
            r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "NetworkThrottlingIndex",
            "REG_DWORD",
            4294967295,
        )
        run(["netsh", "int", "tcp", "set", "global", "autotuninglevel=normal"])
        if self.is_competitive():
            run(["netsh", "int", "tcp", "set", "global", "ecncapability=disabled"])
            run(["netsh", "int", "tcp", "set", "global", "timestamps=disabled"])
        self.log("Rede: módulo concluído.")

    def tweak_input_lag(self):
        self.set_reg(r"HKCU\Control Panel\Mouse", "MouseSpeed", "REG_SZ", "0")
        self.set_reg(r"HKCU\Control Panel\Mouse", "MouseThreshold1", "REG_SZ", "0")
        self.set_reg(r"HKCU\Control Panel\Mouse", "MouseThreshold2", "REG_SZ", "0")
        if self.is_competitive():
            self.snapshot_bcd("disabledynamictick")
            self.snapshot_bcd("useplatformclock")
            run(["bcdedit", "/set", "disabledynamictick", "yes"])
            run(["bcdedit", "/set", "useplatformclock", "false"])
            self.log("bcdedit aplicado.", "AVISO")

    def tweak_ssd(self):
        run(["fsutil", "behavior", "set", "DisableDeleteNotify", "0"])
        run(["schtasks", "/change", "/tn", r"\Microsoft\Windows\Defrag\ScheduledDefrag", "/disable"])
        if self.is_competitive():
            self.set_reg(
                r"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters",
                "EnablePrefetcher",
                "REG_DWORD",
                0,
            )
            run(["powercfg", "-h", "off"])
        self.log("SSD: módulo concluído.")

    def kill_background(self):
        if not self.ask_yesno("Encerrar apps", "Fecha browsers, Spotify, OneDrive, Epic e steamwebhelper. Continuar?"):
            return
        for app in ["chrome.exe", "msedge.exe", "opera.exe", "firefox.exe", "spotify.exe", "OneDrive.exe", "epicgameslauncher.exe", "steamwebhelper.exe"]:
            ok, _ = run(["taskkill", "/f", "/im", app, "/t"])
            self.log(f"{'Encerrado' if ok else 'Ausente'}: {app}")

    def apply_all(self):
        if self.busy:
            return
        msg = (
            "Perfil Competitivo altera VBS, timers e TDR. Continuar?"
            if self.is_competitive()
            else "Aplicar o perfil Equilibrado com backup e ponto de restauro?"
        )
        if not messagebox.askyesno("Aplicar tudo", msg):
            return
        threading.Thread(target=self._apply_all_task, daemon=True).start()

    def _apply_all_task(self):
        self.set_busy(True, "A aplicar otimizações...")
        try:
            self.create_restore_point()
            self.tweak_gpu()
            self.tweak_windows_gaming()
            self.tweak_reg()
            self.tweak_power()
            self.tweak_services()
            self.clean_temp_and_ram()
            self.tweak_network()
            self.tweak_input_lag()
            self.tweak_ssd()
            self.backup.save()
            self.log("Pacote aplicado. Reinicia para kernel/GPU/BCD.")
            self.ui(lambda: messagebox.showinfo("Concluído", "Otimizações aplicadas.\nReinicia o PC se mexeste em kernel/GPU."))
        except Exception as exc:
            self.log(str(exc), "ERRO")
        finally:
            self.set_busy(False)
            self.ui(self.refresh_backup_labels)

    def revert_all(self):
        if self.busy:
            return
        if not self.backup.has_backup():
            messagebox.showinfo("Backup", "Não há backup Mago para restaurar.")
            return
        if not messagebox.askyesno("Restaurar", "Repor os valores guardados no backup Mago?"):
            return
        threading.Thread(target=self._revert_task, daemon=True).start()

    def _revert_task(self):
        self.set_busy(True, "A restaurar backup...")
        try:
            data = deepcopy(self.backup.data)
            for snap in data.get("registry", {}).values():
                key, name = snap["key"], snap["name"]
                if snap.get("existed"):
                    run(["reg", "add", key, "/v", name, "/t", snap["type"], "/d", dword_to_str(snap["data"]), "/f"])
                else:
                    run(["reg", "delete", key, "/v", name, "/f"])
            for snap in data.get("services", {}).values():
                start = snap.get("start") or "demand"
                if start == "unknown":
                    start = "demand"
                run(["sc", "config", snap["name"], "start=", start])
                if snap.get("running"):
                    run(["sc", "start", snap["name"]])
            for bcd_name, snap in data.get("bcd", {}).items():
                if snap.get("existed") and snap.get("value"):
                    run(["bcdedit", "/set", bcd_name, snap["value"]])
                else:
                    run(["bcdedit", "/deletevalue", bcd_name])
            if data.get("power_scheme"):
                run(["powercfg", "-setactive", data["power_scheme"]])
            for token, snap in data.get("startup", {}).items():
                hive, name = token.split("::", 1)
                if snap.get("existed"):
                    run(["reg", "add", hive, "/v", name, "/t", "REG_SZ", "/d", snap.get("data") or "", "/f"])
            self.log("Backup Mago restaurado.")
            self.ui(lambda: messagebox.showinfo("Backup", "Restauro concluído. Reinicia se alteraste BCD/VBS/GPU."))
        except Exception as exc:
            self.log(str(exc), "ERRO")
        finally:
            self.set_busy(False)

    def capture_backup_now(self):
        if self.busy:
            return
        threading.Thread(target=self._capture_backup_task, daemon=True).start()

    def _capture_backup_task(self):
        self.set_busy(True, "A capturar backup...")
        try:
            self.backup.reset()
            for key, name in TRACKED_REGS:
                self.snapshot_reg(key, name, overwrite=True)
            for svc in TRACKED_SERVICES:
                self.snapshot_service(svc, overwrite=True)
            for item in TRACKED_BCD:
                self.snapshot_bcd(item, overwrite=True)
            self.backup.remember_power(self.current_power_guid(), overwrite=True)
            self.backup.data["created"] = datetime.now().isoformat(timespec="seconds")
            self.backup.data["notes"] = ["Captura manual do estado do sistema"]
            self.backup.save()
            os.makedirs(SNAPSHOT_DIR, exist_ok=True)
            stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            dest = os.path.join(SNAPSHOT_DIR, f"mago_backup_{stamp}.json")
            shutil.copy2(BACKUP_FILE, dest)
            self.log(f"Backup guardado em {BACKUP_FILE} e {dest}")
            self.ui(lambda: messagebox.showinfo("Backup", f"Estado atual capturado.\nCópia extra:\n{dest}"))
        except Exception as exc:
            self.log(str(exc), "ERRO")
        finally:
            self.set_busy(False)
            self.ui(self.refresh_backup_labels)

    def export_backup(self):
        if not os.path.exists(BACKUP_FILE):
            messagebox.showinfo("Exportar", "Ainda não existe mago_backup.json. Guarda um backup primeiro.")
            return
        path = filedialog.asksaveasfilename(
            title="Exportar backup",
            defaultextension=".json",
            filetypes=[("JSON", "*.json")],
            initialfile="mago_backup.json",
        )
        if not path:
            return
        shutil.copy2(BACKUP_FILE, path)
        self.log(f"Backup exportado para {path}")

    def import_backup(self):
        path = filedialog.askopenfilename(title="Importar backup", filetypes=[("JSON", "*.json")])
        if not path:
            return
        try:
            with open(path, "r", encoding="utf-8") as handle:
                data = json.load(handle)
            if not isinstance(data, dict) or "registry" not in data:
                messagebox.showerror("Importar", "Ficheiro não parece um backup Mago.")
                return
            shutil.copy2(path, BACKUP_FILE)
            self.backup = BackupStore(BACKUP_FILE)
            self.refresh_backup_labels()
            self.log(f"Backup importado de {path}")
            messagebox.showinfo("Importar", "Backup carregado. Usa Restaurar backup Mago para aplicar.")
        except Exception as exc:
            messagebox.showerror("Importar", str(exc))

    def open_snapshot_dir(self):
        os.makedirs(SNAPSHOT_DIR, exist_ok=True)
        os.startfile(SNAPSHOT_DIR)

    def refresh_backup_labels(self):
        created = self.backup.data.get("created") or "nunca"
        nreg = len(self.backup.data.get("registry") or {})
        nsvc = len(self.backup.data.get("services") or {})
        snaps = 0
        if os.path.isdir(SNAPSHOT_DIR):
            snaps = len([n for n in os.listdir(SNAPSHOT_DIR) if n.endswith(".json")])
        text = (
            f"Último backup Mago: {created}\n"
            f"Chaves de registry: {nreg}  |  Serviços: {nsvc}  |  BCD: {len(self.backup.data.get('bcd') or {})}\n"
            f"Plano de energia: {self.backup.data.get('power_scheme') or '—'}\n"
            f"Snapshots extra na pasta: {snaps}\n"
            f"Ficheiro ativo: {BACKUP_FILE}"
        )
        self.lbl_backup.config(text=text)
        self.refresh_restore_points()

    def make_restore_point(self):
        def _task():
            self.set_busy(True, "Ponto de restauro...")
            try:
                self.create_restore_point()
            finally:
                self.set_busy(False)
                self.ui(self.refresh_restore_points)

        threading.Thread(target=_task, daemon=True).start()

    def refresh_restore_points(self):
        threading.Thread(target=self._load_restore_points, daemon=True).start()

    def _load_restore_points(self):
        cmd = (
            "Get-ComputerRestorePoint | Select-Object SequenceNumber, Description, "
            "@{n='Date';e={$_.ConvertToDateTime($_.CreationTime).ToString('yyyy-MM-dd HH:mm')}}"
        )
        ok, rows, err = ps_json(cmd, timeout=40)

        def _fill():
            self.rp_tree.delete(*self.rp_tree.get_children())
            for row in rows:
                self.rp_tree.insert("", "end", values=(row.get("SequenceNumber"), row.get("Date"), row.get("Description")))

        self.ui(_fill)
        if not rows:
            self.log("Nenhum ponto de restauro listado (Proteção do sistema pode estar desligada).")

    def restore_windows_point(self):
        sel = self.rp_tree.selection()
        if not sel:
            messagebox.showinfo("Restauro", "Seleciona um ponto na lista.")
            return
        seq = str(self.rp_tree.item(sel[0], "values")[0])
        if not messagebox.askyesno("Restauro Windows", f"Isto reinicia o PC e restaura o ponto {seq}. Continuar?"):
            return
        self.log(f"A pedir Restore-Computer {seq}...")
        run(["powershell", "-NoProfile", "-Command", f"Restore-Computer -RestorePoint {seq} -Confirm:$false"], timeout=30)
        messagebox.showinfo("Restauro", "O Windows vai reiniciar se o ponto for aceite.")

    def selected_app(self):
        sel = self.apps_tree.selection()
        if not sel:
            return None
        iid = sel[0]
        for app in self.apps_all:
            if app.get("_id") == iid:
                return app
        return None

    def refresh_apps(self, mark_busy=True):
        if mark_busy:
            self.set_busy(True, "A listar aplicativos...")
        try:
            self.log("A carregar programas instalados...")
            cmd = (
                "$paths=@('HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',"
                "'HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',"
                "'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'); "
                "Get-ItemProperty $paths -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName } | "
                "Select-Object DisplayName,DisplayVersion,Publisher,UninstallString,QuietUninstallString,InstallLocation,PSPath"
            )
            _, win32, _ = ps_json(cmd, timeout=90)
            apps = []
            for item in win32:
                apps.append(
                    {
                        "nome": item.get("DisplayName") or "",
                        "versao": item.get("DisplayVersion") or "",
                        "publisher": item.get("Publisher") or "",
                        "tipo": "Win32",
                        "uninstall": item.get("UninstallString") or "",
                        "quiet": item.get("QuietUninstallString") or "",
                        "location": item.get("InstallLocation") or "",
                        "pspath": item.get("PSPath") or "",
                    }
                )
            cmdx = (
                "Get-AppxPackage | Where-Object { -not $_.NonRemovable } | "
                "Select-Object Name,Version,Publisher,PackageFullName,InstallLocation"
            )
            _, store, _ = ps_json(cmdx, timeout=90)
            for item in store:
                apps.append(
                    {
                        "nome": item.get("Name") or "",
                        "versao": str(item.get("Version") or ""),
                        "publisher": (item.get("Publisher") or "")[:40],
                        "tipo": "Store",
                        "package": item.get("PackageFullName") or "",
                        "location": item.get("InstallLocation") or "",
                    }
                )
            apps.sort(key=lambda a: a["nome"].lower())
            for i, app in enumerate(apps):
                app["_id"] = str(i)
            self.apps_all = apps
            self.ui(self.filter_apps)
            self.log(f"{len(apps)} aplicativos encontrados.")
        except Exception as exc:
            self.log(str(exc), "ERRO")
        finally:
            if mark_busy:
                self.set_busy(False)

    def filter_apps(self):
        query = (self.search_var.get() or "").lower().strip()
        self.apps_tree.delete(*self.apps_tree.get_children())
        visible = 0
        for app in self.apps_all:
            blob = f"{app['nome']} {app['publisher']} {app['tipo']}".lower()
            if query and query not in blob:
                continue
            self.apps_tree.insert("", "end", iid=app["_id"], values=(app["nome"], app["versao"], app["publisher"], app["tipo"]))
            visible += 1
        if hasattr(self, "lbl_apps_status"):
            if not self.apps_all:
                status = "Nenhum aplicativo encontrado. Tenta atualizar a lista."
            elif query and not visible:
                status = f"Nenhum resultado para ‘{query}’."
            else:
                status = f"A mostrar {visible} de {len(self.apps_all)} aplicativos instalados."
            self.lbl_apps_status.config(text=status)

    def uninstall_selected(self):
        app = self.selected_app()
        if not app:
            messagebox.showinfo("Apps", "Seleciona um aplicativo na lista.")
            return
        if not messagebox.askyesno("Desinstalar", f"Desinstalar {app['nome']} pelo método oficial?"):
            return
        threading.Thread(target=lambda: self._uninstall_app(app, force=False), daemon=True).start()

    def force_uninstall_selected(self):
        app = self.selected_app()
        if not app:
            messagebox.showinfo("Apps", "Seleciona um aplicativo na lista.")
            return
        if not messagebox.askyesno(
            "Remoção forçada",
            f"{app['nome']}\n\n"
            "1) tenta o desinstalador oficial\n"
            "2) remove a entrada do Programs and Features\n"
            "3) se a pasta do programa for segura, pergunta se a apaga\n\n"
            "Não usa isto em componentes do Windows. Continuar?",
        ):
            return
        threading.Thread(target=lambda: self._uninstall_app(app, force=True), daemon=True).start()

    def _uninstall_app(self, app, force):
        self.set_busy(True, f"A remover {app['nome'][:28]}...")
        try:
            self.log(f"{'Forçar' if force else 'Desinstalar'}: {app['nome']}")
            if app["tipo"] == "Store":
                pkg = app.get("package")
                ok, out = run(["powershell", "-NoProfile", "-Command", f"Remove-AppxPackage -Package '{pkg}'"], timeout=180)
                self.report(ok, "App Store removida", "Falha ao remover AppX", out)
                if force and not ok:
                    ok2, out2 = run(
                        [
                            "powershell",
                            "-NoProfile",
                            "-Command",
                            f"Get-AppxPackage -AllUsers | Where-Object {{ $_.PackageFullName -eq '{pkg}' }} | Remove-AppxPackage -AllUsers",
                        ],
                        timeout=180,
                    )
                    self.report(ok2, "AppX AllUsers removida", "Falha AllUsers", out2)
            else:
                cmd = (app.get("quiet") or app.get("uninstall") or "").strip()
                if cmd:
                    low = cmd.lower()
                    if "msiexec" in low:
                        guid = re.search(r"\{[0-9A-Fa-f\-]{36}\}", cmd)
                        if guid:
                            cmd = f'msiexec /x {guid.group(0)} /passive /norestart'
                    self.log(f"A executar: {cmd}")
                    ok, out = run(cmd, shell=True, timeout=300)
                    self.report(ok, "Desinstalador terminou", "Desinstalador falhou ou foi cancelado", out)
                elif not force:
                    self.log("Sem UninstallString. Usa remoção forçada.", "AVISO")
                if force:
                    key = pspath_to_reg(app.get("pspath"))
                    if key:
                        ok, out = run(["reg", "delete", key, "/f"])
                        self.report(ok, f"Entrada de registry removida: {key}", "Falha a apagar chave uninstall", out)
                    loc = (app.get("location") or "").strip().strip('"')
                    if loc and os.path.isdir(loc) and is_safe_delete_path(loc):
                        if self.ask_yesno("Apagar pasta", f"Apagar leftover?\n{loc}"):
                            try:
                                shutil.rmtree(loc, ignore_errors=True)
                                self.log(f"Pasta removida: {loc}")
                            except Exception as exc:
                                self.log(str(exc), "ERRO")
                    elif loc:
                        self.log(f"Pasta não apagada (caminho protegido ou inválido): {loc}", "AVISO")
            self.refresh_apps(mark_busy=False)
        except Exception as exc:
            self.log(str(exc), "ERRO")
        finally:
            self.set_busy(False)

    def visual_effects_perf(self):
        self.set_reg(r"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", "REG_DWORD", 2)
        self.log("Efeitos visuais: melhor desempenho (entra após novo login).")

    def clean_shader_cache(self):
        local = os.environ.get("LOCALAPPDATA", "")
        folders = [
            os.path.join(local, "NVIDIA", "DXCache"),
            os.path.join(local, "NVIDIA", "GLCache"),
            os.path.join(local, "D3DSCache"),
            os.path.join(local, "AMD", "DxCache"),
            os.path.join(os.environ.get("USERPROFILE", ""), "AppData", "LocalLow", "NVIDIA", "PerDriverVersion", "DXCache"),
        ]
        for folder in folders:
            if os.path.isdir(folder):
                run(["cmd", "/c", f'del /f /q /s "{folder}\\*.*"'])
                self.log(f"Cache limpo: {folder}")
        self.log("Shaders: o primeiro jogo depois disto pode ir mais lento a compilar.")

    def disable_fast_startup(self):
        self.set_reg(r"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", "REG_DWORD", 0)
        self.log("Arranque rápido desativado.")

    def flush_dns(self):
        ok, out = run(["ipconfig", "/flushdns"])
        self.report(ok, "DNS limpo", "Falha DNS", out)

    def restart_explorer(self):
        if not self.ask_yesno("Explorer", "Reiniciar o Explorador de Ficheiros?"):
            return
        run(["taskkill", "/f", "/im", "explorer.exe"])
        run(["cmd", "/c", "start", "explorer.exe"])
        self.log("Explorer reiniciado.")

    def list_startup(self):
        if self.startup_loading:
            return
        self.startup_loading = True
        threading.Thread(target=self._load_startup, daemon=True).start()

    def _load_startup(self):
        rows = []
        errors = []
        for hive in [r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", r"HKLM\Software\Microsoft\Windows\CurrentVersion\Run"]:
            ok, out = run(["reg", "query", hive])
            if not ok:
                if out:
                    errors.append(f"{hive}: {out[:120]}")
                continue
            short = "HKCU" if hive.startswith("HKCU") else "HKLM"
            for name, value_type, data in parse_reg_values(out):
                rows.append((name, short, data, hive))

        def _fill():
            self.startup_tree.delete(*self.startup_tree.get_children())
            for name, short, data, hive in rows:
                self.startup_tree.insert("", "end", values=(name, short, data), tags=(hive,))

        def _finish():
            self._fill_startup_tree(rows)
            self.startup_loaded = True
            self.startup_loading = False
            if errors:
                self.lbl_startup_status.config(text=f"{len(rows)} itens listados. Não foi possível ler: {'; '.join(errors)}")
            elif rows:
                self.lbl_startup_status.config(text=f"{len(rows)} programas de arranque encontrados.")
            else:
                self.lbl_startup_status.config(text="Nenhum programa de arranque encontrado nas chaves Run do utilizador e do computador.")
            self.log(f"{len(rows)} itens de arranque.")
            for error in errors:
                self.log(error, "AVISO")

        self.ui(_finish)

    def _fill_startup_tree(self, rows):
        self.startup_tree.delete(*self.startup_tree.get_children())
        for name, short, data, hive in rows:
            self.startup_tree.insert("", "end", values=(name, short, data), tags=(hive,))

    def disable_startup_selected(self):
        sel = self.startup_tree.selection()
        if not sel:
            messagebox.showinfo("Arranque", "Seleciona um item na lista.")
            return
        values = self.startup_tree.item(sel[0], "values")
        name = values[0]
        tags = self.startup_tree.item(sel[0], "tags")
        if tags:
            hive = tags[0]
        elif values[1] == "HKLM":
            hive = r"HKLM\Software\Microsoft\Windows\CurrentVersion\Run"
        else:
            hive = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
        if not messagebox.askyesno("Arranque", f"Desativar {name}?"):
            return
        ok, out = run(["reg", "query", hive, "/v", name])
        parsed = parse_reg_query(out, name) if ok else None
        with self.backup.lock:
            self.backup.data.setdefault("startup", {})
            self.backup.data["startup"][f"{hive}::{name}"] = {
                "existed": bool(parsed),
                "data": parsed["data"] if parsed else values[2],
            }
        self.backup.save()
        ok, out = run(["reg", "delete", hive, "/v", name, "/f"])
        self.report(ok, f"Arranque desativado: {name}", "Falha ao desativar arranque", out)
        self.list_startup()


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        problems = run_selfcheck()
        for problem in problems:
            emit(problem)
        if problems:
            sys.exit(3)
        emit("ok")
        sys.exit(0)

    if not check_admin():
        if not relaunch_as_admin():
            ctypes.windll.user32.MessageBoxW(
                0,
                "Esta app precisa de correr como administrador.\n"
                "O pedido de elevacao foi cancelado ou falhou.",
                "MAGO DA PERFORMANCE",
                0x30,
            )
        sys.exit(0)

    root = tk.Tk()
    app = MagoDaPerformanceApp(root)
    root.mainloop()
