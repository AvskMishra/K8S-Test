#!/bin/bash
# Writes k8s-console-tool/kubeconfig-local.yaml, pointing at the port minikube
# already publishes for the API server on WSL's 127.0.0.1 (podman maps the
# node's 8443 there). WSL2 forwards WSL localhost to Windows localhost, so
# the Windows-side app can use it directly — no port-forward tunnel needed,
# and exec/WebSockets work. Re-run after recreating the cluster (the port
# can change); a plain restart normally keeps it.
set -e
OUT="$(cd "$(dirname "$0")/../../k8s-console-tool" && pwd)/kubeconfig-local.yaml"
PORT=$(podman port k8slab 8443 | head -1 | cut -d: -f2)
kubectl config view --raw --minify --flatten \
  | sed "s#server: https://.*#server: https://127.0.0.1:${PORT}#" > "$OUT"
echo "wrote $OUT (server https://127.0.0.1:${PORT})"
