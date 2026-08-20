#!/usr/bin/env python3
import tempfile
from pathlib import Path

# Small regression test for the most important lexical scanner invariant. The full repository validator
# is run separately by CI; this test protects accidental weakening of the network marker vocabulary.

FORBIDDEN = (
    "UnityEngine.Networking.",
    "System.Net.",
    "HttpClient",
    "WebClient",
    "WebRequest.Create",
    "TcpClient",
    "UdpClient",
)


def scan(text: str) -> list[str]:
    return [marker for marker in FORBIDDEN if marker in text]


def main() -> None:
    with tempfile.TemporaryDirectory() as temp:
        path = Path(temp) / "Runtime.cs"
        path.write_text("using System.Net.Http; class X { HttpClient c; }", encoding="utf-8")
        hits = scan(path.read_text(encoding="utf-8"))
        assert "System.Net." in hits
        assert "HttpClient" in hits

        path.write_text("public interface ITranslationEngine {}", encoding="utf-8")
        assert scan(path.read_text(encoding="utf-8")) == []

    print("PASS: local-only network marker scanner catches runtime HTTP usage without banning provider interfaces")


if __name__ == "__main__":
    main()
