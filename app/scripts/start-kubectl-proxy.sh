#!/bin/bash
# Bridges the k8slab API server (only reachable inside WSL2 at 192.168.49.2:8443)
# to a plain local HTTP port. WSL2's automatic localhost forwarding then makes
# it reachable from Windows at http://localhost:8001, with no client certs
# needed on the Windows side since kubectl proxy handles auth server-side.
set -x
pkill -f "kubectl proxy" 2>/dev/null
nohup kubectl proxy --port=8001 --address=0.0.0.0 --accept-hosts='.*' > /tmp/kubectl-proxy.log 2>&1 &
disown
sleep 2
cat /tmp/kubectl-proxy.log
