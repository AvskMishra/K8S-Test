# Example 2 — Commands

## Prerequisites

```bash
minikube start
kubectl cluster-info
```

---

## Deploy

Order matters — namespaces must exist before resources inside them:

```bash
kubectl apply -f 00-namespaces.yaml
kubectl apply -f 01-resource-quota.yaml
kubectl apply -f 02-dev-app.yaml
kubectl apply -f 03-prod-app.yaml
kubectl apply -f 04-monitoring-shared.yaml
```

---

## Verify Namespaces

```bash
# List all namespaces
kubectl get namespaces
kubectl get ns

# Expected:
#   NAME          STATUS
#   default       Active
#   dev           Active
#   kube-node-lease Active
#   kube-public   Active
#   kube-system   Active
#   monitoring    Active
#   prod          Active
```

---

## Verify Resources Per Namespace

```bash
# dev namespace
kubectl get all -n dev
kubectl get pods -n dev
kubectl get services -n dev

# prod namespace
kubectl get all -n prod
kubectl get pods -n prod
kubectl get services -n prod

# monitoring namespace
kubectl get all -n monitoring
kubectl get pods -n monitoring
```

---

## Same Name, Different Namespace — No Conflict

```bash
# dev version (1 replica)
kubectl get deployment webapp -n dev

# prod version (3 replicas)
kubectl get deployment webapp -n prod

# Compare side by side
kubectl get deployment webapp -n dev -o wide
kubectl get deployment webapp -n prod -o wide
```

---

## Check Resource Quotas

```bash
# dev quota usage
kubectl describe resourcequota dev-quota -n dev

# prod quota usage
kubectl describe resourcequota prod-quota -n prod

# List all quotas
kubectl get resourcequota -A
```

---

## Access Apps

```bash
# Access dev webapp (ClusterIP — port-forward since no external access)
kubectl port-forward service/webapp-service 8080:80 -n dev
# Then open: http://localhost:8080

# Access prod webapp (LoadBalancer — minikube exposes it)
minikube service webapp-service -n prod
# or get URL:
minikube service webapp-service -n prod --url

# Access monitoring (ClusterIP — port-forward)
kubectl port-forward service/metrics-service 9090:9090 -n monitoring
# Then open: http://localhost:9090
```

---

## Cross-Namespace DNS — Test Reachability

```bash
# Exec into dev pod and reach monitoring via full DNS name
kubectl exec -it deployment/webapp -n dev -- sh

# Inside the pod:
wget -O- http://metrics-service.monitoring.svc.cluster.local:9090
# or:
curl http://metrics-service.monitoring.svc.cluster.local:9090

# Short name only works in same namespace:
curl http://metrics-service:9090     # works from monitoring namespace
curl http://metrics-service:9090     # fails from dev namespace
```

---

## Set Default Namespace (skip -n every time)

```bash
# Switch default to dev
kubectl config set-context --current --namespace=dev

# Now all commands default to dev
kubectl get pods        # same as: kubectl get pods -n dev

# Switch default to prod
kubectl config set-context --current --namespace=prod

# Reset to default namespace
kubectl config set-context --current --namespace=default
```

---

## Inspect Resources

```bash
# Describe a pod in dev
kubectl describe pod -l app=webapp -n dev

# Describe a pod in prod
kubectl describe pod -l app=webapp -n prod

# Check pod logs in dev
kubectl logs deployment/webapp -n dev

# Check pod logs in prod
kubectl logs deployment/webapp -n prod

# View namespace labels
kubectl get ns --show-labels

# Describe namespace
kubectl describe ns dev
kubectl describe ns prod
kubectl describe ns monitoring
```

---

## Debug

```bash
# Pod not starting?
kubectl describe pod <pod-name> -n dev
kubectl get events -n dev --sort-by='.lastTimestamp'

# Quota exceeded? (pod stuck in Pending)
kubectl describe resourcequota dev-quota -n dev
# Look for: "Used" vs "Hard" — if Used = Hard, quota is full

# Service not reachable from another namespace?
# Check DNS format: <service>.<namespace>.svc.cluster.local
kubectl get service metrics-service -n monitoring
# Verify port: 9090

# Check endpoints (confirms pods behind a service)
kubectl get endpoints webapp-service -n dev
kubectl get endpoints webapp-service -n prod
kubectl get endpoints metrics-service -n monitoring

# All pods across all namespaces
kubectl get pods -A
kubectl get pods --all-namespaces
```

---

## Cleanup

```bash
# Delete all resources in individual namespaces
kubectl delete -f 02-dev-app.yaml
kubectl delete -f 03-prod-app.yaml
kubectl delete -f 04-monitoring-shared.yaml
kubectl delete -f 01-resource-quota.yaml

# Delete namespaces (WARNING: deletes ALL resources inside them)
kubectl delete namespace dev
kubectl delete namespace prod
kubectl delete namespace monitoring

# Or delete everything at once
kubectl delete -f .
```

---

## Flow Recap

```
00-namespaces.yaml
    │
    │  Creates: dev, prod, monitoring
    ▼
01-resource-quota.yaml
    │
    │  dev-quota  → max 5 pods, 1Gi mem  (scoped to dev)
    │  prod-quota → max 20 pods, 8Gi mem (scoped to prod)
    ▼
02-dev-app.yaml
    │
    │  webapp (1 replica) in dev
    │  webapp-service (ClusterIP) in dev
    ▼
03-prod-app.yaml
    │
    │  webapp (3 replicas) in prod
    │  webapp-service (LoadBalancer) in prod
    │
    │  Same name "webapp" — no conflict because namespace=prod
    ▼
04-monitoring-shared.yaml
    │
    │  metrics-collector (prometheus) in monitoring
    │  metrics-service (ClusterIP) in monitoring
    │
    │  Reachable from any namespace via:
    │    metrics-service.monitoring.svc.cluster.local:9090
    ▼
kubectl get pods -A
    # All pods, all namespaces — see full picture
```
