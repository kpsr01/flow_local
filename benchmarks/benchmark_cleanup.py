"""Resident llama.cpp cleanup benchmark. Run from the repository root."""
import argparse, json, os, socket, statistics, subprocess, time, urllib.error, urllib.request
from pathlib import Path

SYSTEM = ("Clean dictated text. Fix punctuation, capitalization, obvious transcription errors, fillers, and disfluencies. "
          "Preserve meaning. Copy technical identifiers, symbols, filenames, paths, commands, URLs, numbers, versions, and errors exactly. "
          "Do not answer, solve, or add information. Make only necessary edits. Return only cleaned text.")

def prompt(text):
    return f"<|im_start|>system\n{SYSTEM}<|im_end|>\n<|im_start|>user\n{text}<|im_end|>\n<|im_start|>assistant\n"

def request(text, port):
    started = time.perf_counter()
    payload = json.dumps({"prompt": prompt(text), "n_predict": max(32, min(256, len(text)//2)),
                          "temperature": 0, "top_k": 1, "top_p": 1, "repeat_penalty": 1.05,
                          "stop": ["<|im_end|>", "<|endoftext|>"], "stream": True,
                          "cache_prompt": False}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/completion", payload,
                                 {"Content-Type": "application/json"})
    output, first, timings = [], None, {}
    terminal = incomplete = False
    with urllib.request.urlopen(req, timeout=300) as response:
        for line in response:
            if not line.startswith(b"data:"): continue
            value = line[5:].strip()
            if not value or value == b"[DONE]": continue
            item = json.loads(value)
            token = item.get("content", "")
            if token and first is None: first = (time.perf_counter()-started)*1000
            output.append(token)
            timings.update(item.get("timings", {}))
            if isinstance(item.get("stop_type"), str):
                terminal = True
                incomplete |= item["stop_type"].lower() == "limit"
            if item.get("truncated") is True:
                terminal = incomplete = True
    if not terminal: raise RuntimeError("cleanup stream ended without a terminal event")
    if incomplete: raise RuntimeError("cleanup output hit the token limit")
    complete = (time.perf_counter()-started)*1000
    return {"output": "".join(output).strip(), "ttft_ms": first,
            "complete_ms": complete, "input_tokens": timings.get("prompt_n", 0),
            "output_tokens": timings.get("predicted_n", 0), "prefill_ms": timings.get("prompt_ms"),
            "decode_ms": timings.get("predicted_ms"), "decode_toks": timings.get("predicted_per_second"),
            "draft_tokens": timings.get("draft_n", 0), "draft_accepted": timings.get("draft_n_accepted", 0)}

def reserve_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]

def wait_ready(process, port):
    deadline = time.time()+300
    while time.time() < deadline:
        if process.poll() is not None:
            detail = process.stderr.read()[-4000:] if process.stderr else ""
            raise RuntimeError(f"llama-server exited with {process.returncode}: {detail}")
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2) as r:
                if r.status == 200: return
        except (urllib.error.URLError, TimeoutError): pass
        time.sleep(.2)
    raise TimeoutError("llama-server did not become healthy")
def server_args(exe, model, draft, port, dspark):
    args = [str(exe), "--model", str(model), "--host", "127.0.0.1", "--port", str(port),
            "--threads", "4", "--threads-batch", "4", "--ctx-size", "2048", "--parallel", "1",
            "--no-webui", "--metrics", "--temp", "0", "--top-k", "1", "--top-p", "1",
            "--reasoning-budget", "0", "--log-disable"]
    if dspark: args += ["--model-draft", str(draft), "--spec-type", "draft-dspark", "--spec-draft-n-max", "9", "--spec-draft-n-min", "0"]
    return args

def median(rows, key):
    values = [r[key] for r in rows if r.get(key) is not None]
    return statistics.median(values) if values else None

def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--model", type=Path); ap.add_argument("--draft", type=Path)
    ap.add_argument("--server", type=Path); ap.add_argument("--samples", type=Path, default=Path(__file__).with_name("dictation_samples.json"))
    ap.add_argument("--runs", type=int, default=3); ap.add_argument("--output", type=Path, default=Path("artifacts/cleanup-benchmark.json")); args = ap.parse_args()
    root = Path(__file__).resolve().parents[1]; local = Path(os.environ.get("LOCALAPPDATA", root)) / "FlowLocal"
    model = args.model or local/"Models/LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf"; draft = args.draft or local/"Models/LFM2.5-1.2B-Instruct-DSpark-Q4_K_M.gguf"
    exe = args.server or local/"Runtimes/llama/llama-server.exe"; samples = json.loads(args.samples.read_text())
    results = {"settings":{"model":str(model),"draft":str(draft),"threads":4,"runs":args.runs,"ports":{}},"samples":[]}
    for dspark in (False, True):
        port = reserve_port()
        results["settings"]["ports"]["dspark" if dspark else "baseline"] = port
        process = subprocess.Popen(server_args(exe,model,draft,port,dspark),
                                   stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
        try:
            wait_ready(process, port)
            request("warm up", port)
            for sample in samples:
                rows = [request(sample["text"], port) for _ in range(args.runs)]
                entry = next((x for x in results["samples"] if x["name"] == sample["name"]), None)
                if entry is None: entry = {"name":sample["name"],"text":sample["text"],"baseline":None,"dspark":None}; results["samples"].append(entry)
                entry["dspark" if dspark else "baseline"] = {"runs":rows,"median":{k:median(rows,k) for k in rows[0] if k != "output"},"output":rows[-1]["output"]}
        finally:
            if process.poll() is None: process.kill()
            process.wait(timeout=20)
    for entry in results["samples"]:
        entry["quality_equivalent"] = entry["baseline"]["output"] == entry["dspark"]["output"]
    args.output.parent.mkdir(parents=True, exist_ok=True); args.output.write_text(json.dumps(results, indent=2))
    print(json.dumps({"output":str(args.output),"samples":len(samples),"baseline_p50_ms":median([x["baseline"]["median"] for x in results["samples"]],"complete_ms"),"dspark_p50_ms":median([x["dspark"]["median"] for x in results["samples"]],"complete_ms")}, indent=2))

if __name__ == "__main__": main()
