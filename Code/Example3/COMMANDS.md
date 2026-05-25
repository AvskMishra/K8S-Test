# Example 3 — Commands

## Prerequisites

```bash
# Start minikube
minikube start

# Enable the nginx Ingress controller (one-time setup)
minikube addons enable ingress

# Verify controller pod is running (wait ~60s after enabling)
kubectl get pods -n ingress-nginx
# Expected: ingress-nginx-controller-... Running
```

---

## Deploy Everything

```bash
# Apply in order
kubectl apply -f 01-my-app-deployment.yaml
kubectl apply -f 02-my-app-service.yaml
kubectl apply -f 03-my-api-deployment.yaml
kubectl apply -f 04-my-api-service.yaml
kubectl apply -f 05-my-app-ingress.yaml
```

Or apply everything at once:
```bash
kubectl apply -f .
```

---

## Verify Resources

```bash
# Pods — wait until all are Running
kubectl get pods
# Expected:
#   my-app-xxxx   Running  (x2)
#   my-api-xxxx   Running  (x1)

# Services
kubectl get services
# Expected:
#   my-app-service   ClusterIP   ...   80/TCP
#   my-api-service   ClusterIP   ...   80/TCP

# Ingress — ADDRESS column should fill in after ~30s
kubectl get ingress
# Expected:
#   NAME             CLASS   HOSTS       ADDRESS        PORTS
#   my-app-ingress   nginx   myapp.com   192.168.x.x    80

# Full details
kubectl describe ingress my-app-ingress
```

---

## Map hostname to minikube IP

```bash
# Get minikube IP
minikube ip
# Example output: 192.168.49.2

# Add to hosts file (run as Administrator on Windows / sudo on Linux/Mac)
# Windows: C:\Windows\System32\drivers\etc\hosts
# Linux/Mac: /etc/hosts

# Add this line (replace IP with your minikube ip output):
192.168.49.2  myapp.com
```

**Windows (PowerShell as Administrator):**
```powershell
Add-Content -Path "C:\Windows\System32\drivers\etc\hosts" -Value "$(minikube ip)  myapp.com"
```

---

## Test in Browser

```
http://myapp.com/         → green page: "my-app is alive!"
http://myapp.com/api      → blue page:  "my-api is alive!"
http://myapp.com/unknown  → 404 (no matching rule, no default backend)
```

---

## Test with curl

```bash
curl http://myapp.com/
curl http://myapp.com/api
```

Or use minikube tunnel (alternative approach):
```bash
minikube tunnel
# Then access via localhost if using LoadBalancer — not needed here
```

---

## Debug Commands

```bash
# Check ingress controller logs
kubectl logs -n ingress-nginx deployment/ingress-nginx-controller

# Check if ingress rules loaded
kubectl describe ingress my-app-ingress

# Check service endpoints (confirms pods are registered)
kubectl get endpoints my-app-service
kubectl get endpoints my-api-service

# Exec into a pod to test internally
kubectl exec -it <my-app-pod-name> -- curl localhost

# Check events if something is wrong
kubectl get events --sort-by='.lastTimestamp'
```

---

## Cleanup

```bash
kubectl delete -f .
# Removes all deployments, services, configmaps, and ingress resource
# Does NOT remove the ingress controller (it's a minikube addon)
```

---

## Flow Recap

```
Browser: http://myapp.com/api
    │
    │  (hosts file maps myapp.com → minikube IP)
    ▼
Ingress Controller Pod (nginx, ingress-nginx namespace)
    │  reads my-app-ingress rules
    │  path /api → my-api-service:80
    ▼
my-api-service (ClusterIP)
    │  selects pod with label app=my-api
    ▼
my-api pod
    │  nginx serves index.html from ConfigMap
    ▼
Response: blue page "my-api is alive!"
```
