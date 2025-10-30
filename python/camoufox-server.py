#!/usr/bin/env python3
"""
CamouFox Server Launcher
Launches CamouFox browser with provided configuration and prints CDP URL.
"""
import sys
import json

from camoufox.server import launch_server
from browserforge.fingerprints import Screen


def main():
    """Launch CamouFox browser and print CDP URL."""
    # Read config from command line argument or stdin
    if len(sys.argv) > 1:
        config_path = sys.argv[1]
        with open(config_path, 'r', encoding='utf-8') as f:
            config = json.load(f)
    else:
        # Read from stdin
        config = json.load(sys.stdin)
    
    constrains = Screen(max_width=config['screen']['maxWidth'], max_height=config['screen']['maxHeight'])
    # Launch CamouFox with configuration
    launch_server(
        headless=False,
        geoip=True,
        os = config['os'],
        screen = constrains,
        persistent_context=True,
        user_data_dir=config['user_data_dir']
    )

if __name__ == '__main__':
    main()