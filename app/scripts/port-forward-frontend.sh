#!/bin/bash
# Forwards the in-cluster frontend Service to a WSL2 port. WSL2 automatically
# forwards this to Windows localhost, so the app becomes reachable at
# http://localhost:8090 from your Windows browser without needing a real
# LoadBalancer/Ingress (those come later in the infra roadmap).
set -x
pkill -f "port-forward svc/product-frontend" 2>/dev/null
nohup kubectl -n product-catalog port-forward svc/product-frontend 8090:8080 --address 0.0.0.0 > /tmp/port-forward.log 2>&1 &
disown
sleep 2
cat /tmp/port-forward.log
