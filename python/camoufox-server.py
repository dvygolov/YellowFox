#!/usr/bin/env python3
"""
CamouFox Server Launcher
Launches CamouFox browser with provided configuration and prints CDP URL.
"""
import sys
import json
import asyncio
from camoufox.async_api import Camoufox


async def main():
    """Launch CamouFox browser and print CDP URL."""
    # Read config from command line argument or stdin
    if len(sys.argv) > 1:
        config_path = sys.argv[1]
        with open(config_path, 'r', encoding='utf-8') as f:
            config = json.load(f)
    else:
        # Read from stdin
        config = json.load(sys.stdin)
    
    # Extract profile directory from config
    user_data_dir = config.pop('user_data_dir', None)
    
    # Launch CamouFox with configuration
    browser = await Camoufox(
        config=config,
        headless=False,
        user_data_dir=user_data_dir
    ).__aenter__()
    
    # Print CDP URL for .NET to connect
    print(browser.cdp_url, flush=True)
    print(f"DEBUG:PID={browser.process.pid}", flush=True, file=sys.stderr)
    
    # Keep the browser running
    # The .NET app will manage the browser lifecycle via CDP
    try:
        await asyncio.Event().wait()
    except (KeyboardInterrupt, SystemExit):
        await browser.__aexit__(None, None, None)


if __name__ == '__main__':
    asyncio.run(main())
