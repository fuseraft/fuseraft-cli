#!/usr/bin/env python3
"""Capture the real chat-completion request fuseraft sends to a model provider.

Stands in for the model endpoint so you can inspect exactly what fuseraft actually
sends — most usefully the `tools` array, to verify which functions an agent's
Capabilities/Plugins config truly exposes (tool-level enforcement), as opposed to
inferring it from what the model happened to call.

Usage:
  1. Point the agent's Model.Endpoint at this server in your config, e.g.:
       Model:
         Endpoint: http://127.0.0.1:8765/v1
         Provider: openai
         ApiKeyEnvVar: ANY_VAR_THAT_IS_SET   # auth is not checked, just needs to resolve
  2. python3 scripts/capture_model_request.py [port] [output.json]
  3. In another shell: fuseraft run --config your-config.yaml --no-banner "anything"
     (it will exit/error after this server's canned reply — that's expected, the request
     is already captured by then).

Note: the OpenAI-compatible client probes `GET /v1/models` once before the first chat
completion call — this server answers POST only, so that probe gets a harmless 501 and
the real request still arrives right after. Don't use the single-request http.server
pattern here; it would consume that probe and never see the real call.
"""
import http.server
import json
import socketserver
import sys

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
OUT  = sys.argv[2] if len(sys.argv) > 2 else "captured_request.json"


class Handler(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length)
        with open(OUT, "wb") as f:
            f.write(body)

        request = json.loads(body)
        tool_names = sorted(
            t["function"]["name"] for t in request.get("tools", []) if "function" in t
        )
        print(f"\nCaptured request -> {OUT}")
        print(f"Tools offered ({len(tool_names)}):")
        for name in tool_names:
            print(f"  - {name}")

        # Minimal valid OpenAI-compatible reply — plain text, no tool call — just enough
        # for the client library to parse without throwing. BLOCKED halts the agent
        # cleanly instead of looping on a follow-up turn.
        reply = {
            "id": "capture-1",
            "object": "chat.completion",
            "created": 0,
            "model": "capture",
            "choices": [{
                "index": 0,
                "message": {"role": "assistant", "content": "BLOCKED\ncaptured for inspection, halting here."},
                "finish_reason": "stop",
            }],
            "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
        }
        data = json.dumps(reply).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, fmt, *args):
        pass  # quiet — the tool-list summary above is the useful output


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


if __name__ == "__main__":
    print(f"Listening on 127.0.0.1:{PORT} — point Model.Endpoint at http://127.0.0.1:{PORT}/v1")
    print("Ctrl-C to stop.")
    try:
        Server(("127.0.0.1", PORT), Handler).serve_forever()
    except KeyboardInterrupt:
        pass
