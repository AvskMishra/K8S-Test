#!/bin/bash
# Run as root inside WSL2 Ubuntu. Runs the ProductApi container on the same
# network as mongo-dev, resolving Mongo by its network alias "mongo".
set -ex
podman rm -f product-api-dev 2>/dev/null || true
podman run -d \
  --name product-api-dev \
  --network appnet \
  -p 8080:8080 \
  -e MongoDbSettings__ConnectionString="mongodb://mongo:27017" \
  -e MongoDbSettings__DatabaseName="ProductCatalog" \
  product-api:dev
