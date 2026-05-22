#!/usr/bin/env python3
"""Local unauthenticated SOCKS5 bridge to an authenticated upstream SOCKS5 proxy."""

import asyncio
import json
import socket
import sys


async def read_exact(reader, count):
    return await reader.readexactly(count)


async def read_socks_address(reader):
    atyp = (await read_exact(reader, 1))[0]
    if atyp == 1:
        host = socket.inet_ntop(socket.AF_INET, await read_exact(reader, 4))
        raw_host = bytes([atyp]) + socket.inet_pton(socket.AF_INET, host)
    elif atyp == 3:
        length = (await read_exact(reader, 1))[0]
        host_bytes = await read_exact(reader, length)
        host = host_bytes.decode("idna")
        raw_host = bytes([atyp, length]) + host_bytes
    elif atyp == 4:
        host = socket.inet_ntop(socket.AF_INET6, await read_exact(reader, 16))
        raw_host = bytes([atyp]) + socket.inet_pton(socket.AF_INET6, host)
    else:
        raise ValueError("Unsupported address type")

    port_bytes = await read_exact(reader, 2)
    port = int.from_bytes(port_bytes, "big")
    return raw_host, port_bytes, host, port


async def drain_reply_address(reader):
    atyp = (await read_exact(reader, 1))[0]
    if atyp == 1:
        await read_exact(reader, 4)
    elif atyp == 3:
        length = (await read_exact(reader, 1))[0]
        await read_exact(reader, length)
    elif atyp == 4:
        await read_exact(reader, 16)
    else:
        raise ValueError("Unsupported reply address type")
    await read_exact(reader, 2)


async def authenticate_upstream(reader, writer, username, password):
    if username or password:
        writer.write(b"\x05\x01\x02")
        await writer.drain()
        response = await read_exact(reader, 2)
        if response != b"\x05\x02":
            raise ConnectionError("Upstream SOCKS5 proxy did not accept username/password auth")

        username_bytes = username.encode("utf-8")
        password_bytes = password.encode("utf-8")
        if len(username_bytes) > 255 or len(password_bytes) > 255:
            raise ValueError("SOCKS5 username/password is too long")

        writer.write(
            b"\x01"
            + bytes([len(username_bytes)])
            + username_bytes
            + bytes([len(password_bytes)])
            + password_bytes
        )
        await writer.drain()
        auth_response = await read_exact(reader, 2)
        if auth_response != b"\x01\x00":
            raise ConnectionError("Upstream SOCKS5 authentication failed")
    else:
        writer.write(b"\x05\x01\x00")
        await writer.drain()
        response = await read_exact(reader, 2)
        if response != b"\x05\x00":
            raise ConnectionError("Upstream SOCKS5 proxy did not accept no-auth mode")


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
    upstream_reader = None
    upstream_writer = None
    try:
        header = await read_exact(client_reader, 2)
        if header[0] != 5:
            raise ValueError("Only SOCKS5 is supported")
        await read_exact(client_reader, header[1])
        client_writer.write(b"\x05\x00")
        await client_writer.drain()

        request_header = await read_exact(client_reader, 3)
        if request_header[0] != 5 or request_header[1] != 1:
            client_writer.write(b"\x05\x07\x00\x01\x00\x00\x00\x00\x00\x00")
            await client_writer.drain()
            return

        raw_host, raw_port, _, _ = await read_socks_address(client_reader)
        upstream_reader, upstream_writer = await asyncio.open_connection(
            config["host"],
            int(config["port"]),
        )
        await authenticate_upstream(
            upstream_reader,
            upstream_writer,
            config.get("username") or "",
            config.get("password") or "",
        )

        upstream_writer.write(b"\x05\x01\x00" + raw_host + raw_port)
        await upstream_writer.drain()
        response_header = await read_exact(upstream_reader, 3)
        await drain_reply_address(upstream_reader)
        if response_header[1] != 0:
            client_writer.write(b"\x05\x01\x00\x01\x00\x00\x00\x00\x00\x00")
            await client_writer.drain()
            return

        client_writer.write(b"\x05\x00\x00\x01\x00\x00\x00\x00\x00\x00")
        await client_writer.drain()
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
        raise SystemExit("Usage: socks5-auth-bridge.py <config.json>")

    with open(sys.argv[1], "r", encoding="utf-8") as file:
        config = json.load(file)

    server = await asyncio.start_server(
        lambda reader, writer: handle_client(reader, writer, config),
        "127.0.0.1",
        0,
    )
    port = server.sockets[0].getsockname()[1]
    print(f"socks5://127.0.0.1:{port}", flush=True)

    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
