#!/usr/bin/env python3
"""Local unauthenticated HTTP proxy bridge to an authenticated upstream HTTP proxy."""

import asyncio
import base64
import json
import sys


HEADER_LIMIT = 1024 * 1024


async def read_headers(reader):
    data = bytearray()
    while b"\r\n\r\n" not in data:
        chunk = await reader.read(4096)
        if not chunk:
            break
        data.extend(chunk)
        if len(data) > HEADER_LIMIT:
            raise ValueError("HTTP header block is too large")

    header_end = data.find(b"\r\n\r\n")
    if header_end < 0:
        return bytes(data), b""

    return bytes(data[: header_end + 4]), bytes(data[header_end + 4 :])


def build_proxy_authorization(username, password):
    token = f"{username}:{password}".encode("utf-8")
    return "Basic " + base64.b64encode(token).decode("ascii")


def rewrite_request(header_bytes, authorization):
    text = header_bytes.decode("iso-8859-1")
    lines = text.split("\r\n")
    if not lines or not lines[0]:
        raise ValueError("Invalid HTTP request")

    request_line = lines[0]
    rewritten = [request_line]
    has_host = False
    for line in lines[1:]:
        if not line:
            continue

        name = line.split(":", 1)[0].strip().lower()
        if name == "host":
            has_host = True
        if name in ("proxy-authorization", "proxy-connection"):
            continue
        rewritten.append(line)

    if request_line.upper().startswith("CONNECT ") and not has_host:
        target = request_line.split(" ", 2)[1]
        rewritten.append(f"Host: {target}")

    rewritten.append(f"Proxy-Authorization: {authorization}")
    rewritten.append("Proxy-Connection: keep-alive")
    rewritten.append("")
    rewritten.append("")
    return "\r\n".join(rewritten).encode("iso-8859-1")


async def relay(reader, writer):
    try:
        while True:
            chunk = await reader.read(65536)
            if not chunk:
                break
            writer.write(chunk)
            await writer.drain()
    finally:
        try:
            writer.close()
            await writer.wait_closed()
        except Exception:
            pass


async def handle_client(client_reader, client_writer, config):
    upstream_writer = None
    try:
        header_bytes, initial_body = await read_headers(client_reader)
        if not header_bytes:
            return

        authorization = build_proxy_authorization(
            config.get("username") or "",
            config.get("password") or "",
        )
        upstream_reader, upstream_writer = await asyncio.open_connection(
            config["host"],
            int(config["port"]),
        )

        upstream_writer.write(rewrite_request(header_bytes, authorization))
        if initial_body:
            upstream_writer.write(initial_body)
        await upstream_writer.drain()

        await asyncio.gather(
            relay(client_reader, upstream_writer),
            relay(upstream_reader, client_writer),
        )
    except Exception:
        try:
            client_writer.close()
            await client_writer.wait_closed()
        except Exception:
            pass
        if upstream_writer is not None:
            try:
                upstream_writer.close()
                await upstream_writer.wait_closed()
            except Exception:
                pass


async def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: http-auth-bridge.py <config.json>")

    with open(sys.argv[1], "r", encoding="utf-8") as file:
        config = json.load(file)

    server = await asyncio.start_server(
        lambda reader, writer: handle_client(reader, writer, config),
        "127.0.0.1",
        0,
    )
    port = server.sockets[0].getsockname()[1]
    print(f"http://127.0.0.1:{port}", flush=True)

    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
