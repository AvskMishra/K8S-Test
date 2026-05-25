# Example 1 — Commands

## Prerequisites

```bash
# Start minikube
minikube start

# Verify cluster is running
kubectl cluster-info
kubectl get nodes
```

---

## Deploy

Apply in order — Secret and ConfigMap must exist before pods start:

```bash
kubectl apply -f mongo-secret.yaml
kubectl apply -f mongo-configmap.yaml
kubectl apply -f mongo-deployment.yaml
kubectl apply -f mongo-service.yaml
kubectl apply -f mongoexpress-deployment.yaml
```

Or apply all at once (order is still respected by dependencies, but pods may start before Secret exists):
```bash
# Safe to use only after the first deploy — Secret already exists
kubectl apply -f .
```

---

## Verify Resources

```bash
# Check all pods are Running
kubectl get pods
# Expected:
#   mongodb-deployment-xxxx      Running
#   mongoexpress-deployment-xxxx Running

# Check services
kubectl get services
# Expected:
#   mongodb-service        ClusterIP   ...   27017/TCP
#   mongoexpress-service   LoadBalancer ...  8081:30000/TCP

# Check secret exists
kubectl get secret mongodb-secret

# Check configmap exists
kubectl get configmap mongodb-configmap

# Check all deployments
kubectl get deployments
```

---

## Access Mongo Express

```bash
# Open Mongo Express in browser (Minikube handles the URL)
minikube service mongoexpress-service
```

Or get the URL without opening:
```bash
minikube service mongoexpress-service --url
```

Login credentials:
- **Username**: `mongouser`
- **Password**: `mongopass`

---

## Inspect Resources

```bash
# Full details of MongoDB deployment
kubectl describe deployment mongodb-deployment

# Full details of Mongo Express deployment
kubectl describe deployment mongoexpress-deployment

# Check pod logs — MongoDB
kubectl logs deployment/mongodb-deployment

# Check pod logs — Mongo Express
kubectl logs deployment/mongoexpress-deployment

# Describe a service
kubectl describe service mongodb-service
kubectl describe service mongoexpress-service

# Decode secret values (verify credentials)
kubectl get secret mongodb-secret -o jsonpath='{.data.mongo-root-username}' | base64 --decode
kubectl get secret mongodb-secret -o jsonpath='{.data.mongo-root-password}' | base64 --decode

# View configmap values
kubectl get configmap mongodb-configmap -o yaml
```

---

## Debug

```bash
# Pod not starting? Check events
kubectl describe pod <pod-name>
kubectl get events --sort-by='.lastTimestamp'

# Pod in CreateContainerConfigError?
# → Secret or ConfigMap missing. Apply them first.
kubectl get secret
kubectl get configmap

# Mongo Express can't connect to MongoDB?
# → Check mongodb-service exists and has endpoints
kubectl get endpoints mongodb-service

# Exec into MongoDB pod
kubectl exec -it deployment/mongodb-deployment -- bash

# Exec into Mongo Express pod
kubectl exec -it deployment/mongoexpress-deployment -- sh

# Test MongoDB connection from inside Mongo Express pod
kubectl exec -it deployment/mongoexpress-deployment -- sh -c 'curl mongodb-service:27017'
```

---

## Update Credentials

To change MongoDB credentials:

```powershell
# Encode new values (PowerShell)
[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("newusername"))
[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("newpassword"))
```

Edit `mongo-secret.yaml` with new base64 values, then:

```bash
kubectl apply -f mongo-secret.yaml
# Restart pods to pick up new secret values
kubectl rollout restart deployment/mongodb-deployment
kubectl rollout restart deployment/mongoexpress-deployment
```

---

## Cleanup

```bash
# Delete all resources from this example
kubectl delete -f .

# Or delete individually
kubectl delete deployment mongodb-deployment
kubectl delete deployment mongoexpress-deployment
kubectl delete service mongodb-service
kubectl delete service mongoexpress-service
kubectl delete secret mongodb-secret
kubectl delete configmap mongodb-configmap
```

---

## Flow Recap

```
kubectl apply -f mongo-secret.yaml
kubectl apply -f mongo-configmap.yaml
      │
      │  Both exist before any pod starts
      ▼
kubectl apply -f mongo-deployment.yaml
      │
      │  MongoDB pod reads:
      │    MONGO_INITDB_ROOT_USERNAME ← secret/mongodb-secret → mongo-root-username
      │    MONGO_INITDB_ROOT_PASSWORD ← secret/mongodb-secret → mongo-root-password
      ▼
kubectl apply -f mongo-service.yaml
      │
      │  Creates DNS: mongodb-service → ClusterIP → MongoDB pod
      ▼
kubectl apply -f mongoexpress-deployment.yaml
      │
      │  Mongo Express pod reads:
      │    ME_CONFIG_MONGODB_ADMINUSERNAME ← secret/mongodb-secret → mongo-root-username
      │    ME_CONFIG_MONGODB_ADMINPASSWORD ← secret/mongodb-secret → mongo-root-password
      │    ME_CONFIG_MONGODB_SERVER        ← configmap/mongodb-configmap → database_url
      │
      │  Connects to: mongodb-service:27017
      ▼
minikube service mongoexpress-service
      │
      │  Opens: http://<minikube-ip>:30000
      ▼
Mongo Express UI in browser
```
