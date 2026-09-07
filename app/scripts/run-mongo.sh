#!/bin/bash
# Run as root inside WSL2 Ubuntu. Starts a MongoDB container for local app dev,
# on a dedicated network so the API container can reach it by name "mongo".
set -ex
podman network create appnet 2>/dev/null || true
podman rm -f mongo-dev 2>/dev/null || true
podman run -d \
  --name mongo-dev \
  --network appnet \
  --network-alias mongo \
  -p 27017:27017 \
  -v mongo-dev-data:/data/db \
  docker.io/library/mongo:7
