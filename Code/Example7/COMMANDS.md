# Example 7 — Commands

## Apply

```bash
# Deploy app pods first
kubectl apply -f 01-sample-app-deployment.yaml

# Apply each service type
kubectl apply -f 02-clusterip-service.yaml
kubectl apply -f 03-headless-service.yaml
kubectl apply -f 04-nodeport-service.yaml
kubectl apply -f 05-loadbalancer-service.yaml

# Or all 4 on same app for comparison
kubectl apply -f 06-all-services-comparison.yaml
```

## Inspect Services

```bash
# List all — compare TYPE and CLUSTER-IP and EXTERNAL-IP
kubectl get svc

# Describe a service — see Endpoints, NodePort, selector
kubectl describe svc backend-api
kubectl describe svc web-app-lb

# Get raw YAML back
kubectl get svc backend-api -o yaml
```

## Inspect Endpoints (pod IPs behind service)

```bash
# ClusterIP — shows pod IPs, kube-proxy routes to them
kubectl get endpoints backend-api

# Headless — same, but DNS returns these directly (no VIP)
kubectl get endpoints web-app-headless

# Watch endpoints change as pods scale
kubectl get endpoints web-app-clusterip --watch
```

## DNS Verification (run from inside cluster)

```bash
# Start a debug pod
kubectl run dns-test --image=busybox --rm -it --restart=Never -- sh

# Inside pod:
# ClusterIP — returns single VIP
nslookup web-app-clusterip.default.svc.cluster.local

# Headless — returns multiple pod IPs
nslookup web-app-headless.default.svc.cluster.local

# StatefulSet per-pod DNS
nslookup db-0.db.default.svc.cluster.local
nslookup db-1.db.default.svc.cluster.local
```

## Test Connectivity

```bash
# ClusterIP from inside cluster
kubectl run curl-test --image=curlimages/curl --rm -it -- \
  curl http://web-app-clusterip.default.svc.cluster.local

# NodePort — from outside (replace with your node IP)
curl http://<node-ip>:30080

# NodePort — minikube
minikube service web-app-nodeport --url
curl $(minikube service web-app-nodeport --url)

# LoadBalancer — wait for EXTERNAL-IP then curl
kubectl get svc web-app-lb --watch
curl http://<EXTERNAL-IP>
```

## Scale and Watch Endpoints Update

```bash
# Scale deployment
kubectl scale deployment web-app --replicas=5

# Watch endpoints add the new pod IPs
kubectl get endpoints web-app-clusterip --watch
```

## StatefulSet Headless DNS

```bash
# Apply headless + statefulset
kubectl apply -f 03-headless-service.yaml

# Watch pods come up with stable names (db-0, db-1, db-2)
kubectl get pods -l app=db --watch

# Verify per-pod DNS
kubectl run dns-test --image=busybox --rm -it -- nslookup db-0.db.default.svc.cluster.local
```

## Cleanup

```bash
kubectl delete -f .
```
