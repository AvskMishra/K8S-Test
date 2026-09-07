#!/bin/bash
set -ex
cd /mnt/c/CodeBase/app/frontend
podman build -t product-frontend:dev .
