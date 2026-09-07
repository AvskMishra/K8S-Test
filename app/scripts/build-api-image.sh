#!/bin/bash
# Run as root inside WSL2 Ubuntu. Builds the ProductApi container image
# from the Windows-side source via the /mnt/c passthrough.
set -ex
cd /mnt/c/CodeBase/app/backend/ProductApi
podman build -t product-api:dev .
