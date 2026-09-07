#!/bin/bash
# Raw TCP tunnel straight to the API server's HTTPS port (unlike
# `kubectl proxy`, which is an HTTP-only reverse proxy and doesn't properly
# support the WebSocket upgrade that exec/attach need). port-forward tunnels
# TCP bytes untouched, so TLS + WebSocket semantics survive completely.
set -x
pkill -f "port-forward" 2>/dev/null
nohup kubectl port-forward pod/kube-apiserver-k8slab 6443:8443 -n kube-system --address 0.0.0.0 > /tmp/api-tunnel.log 2>&1 &
disown
sleep 2
cat /tmp/api-tunnel.log

echo "--- writing a flattened, portable kubeconfig for Windows-side use ---"
kubectl config view --raw --minify --flatten > /tmp/kubeconfig-direct.yaml
sed -i 's#server: https://.*#server: https://localhost:6443#' /tmp/kubeconfig-direct.yaml
cp /tmp/kubeconfig-direct.yaml /mnt/c/CodeBase/k8s-console-tool/kubeconfig-direct.yaml
echo "wrote C:\CodeBase\k8s-console-tool\kubeconfig-direct.yaml"
