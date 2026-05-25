# Namespace Commands — Quick Reference

## Apply all files in order
```bash
kubectl apply -f 00-namespaces.yaml
kubectl apply -f 01-resource-quota.yaml
kubectl apply -f 02-dev-app.yaml
kubectl apply -f 03-prod-app.yaml
kubectl apply -f 04-monitoring-shared.yaml
```

---

## View namespaces
```bash
kubectl get namespaces
# or shorthand:
kubectl get ns
```

---

## List resources INSIDE a specific namespace
```bash
# pods in dev
kubectl get pods -n dev

# pods in prod
kubectl get pods -n prod

# pods in monitoring
kubectl get pods -n monitoring

# all pods in ALL namespaces
kubectl get pods --all-namespaces
# or shorthand:
kubectl get pods -A
```

---

## KEY POINT: Same name, different namespace — no conflict
```bash
kubectl get deployment webapp -n dev    # dev version (1 replica)
kubectl get deployment webapp -n prod   # prod version (3 replicas)
```

---

## Check resource quotas
```bash
kubectl describe resourcequota dev-quota -n dev
kubectl describe resourcequota prod-quota -n prod
```

---

## Cross-namespace DNS — how services find each other
```
Format: <service>.<namespace>.svc.cluster.local

Examples:
  webapp-service.dev.svc.cluster.local        → dev app
  webapp-service.prod.svc.cluster.local       → prod app
  metrics-service.monitoring.svc.cluster.local → prometheus
```

---

## Set default namespace (so you don't type -n every time)
```bash
kubectl config set-context --current --namespace=dev
# now all commands default to dev namespace

kubectl config set-context --current --namespace=default
# reset back to default
```

---

## Delete entire namespace (deletes ALL resources inside it)
```bash
# WARNING: deletes everything in the namespace
kubectl delete namespace dev
```

---

## Namespace isolation summary
```
┌─────────────────────────────────────────────────┐
│                  K8s Cluster                     │
│                                                  │
│  ┌──────────┐  ┌──────────┐  ┌──────────────┐  │
│  │   dev    │  │   prod   │  │  monitoring  │  │
│  │          │  │          │  │              │  │
│  │ webapp   │  │ webapp   │  │  prometheus  │  │
│  │ (1 pod)  │  │ (3 pods) │  │  (1 pod)     │  │
│  │          │  │          │  │              │  │
│  │ quota:   │  │ quota:   │  │  shared by   │  │
│  │ 5 pods   │  │ 20 pods  │  │  dev + prod  │  │
│  │ 1Gi mem  │  │ 8Gi mem  │  │              │  │
│  └──────────┘  └──────────┘  └──────────────┘  │
│                                                  │
│  Namespaces are isolated but can communicate    │
│  via full DNS: svc.namespace.svc.cluster.local  │
└─────────────────────────────────────────────────┘
```
