# Example 5 — Commands Reference (PowerShell / CMD)

## Prerequisites Check

```powershell
# Verify tools installed
kubectl version --client
minikube version

# Start minikube (if not running)
minikube start

# Confirm cluster is up
kubectl cluster-info
kubectl get nodes
```

---

## Step 1 — Local PV + PVC

### Prepare node directory (minikube only)

```powershell
# SSH into minikube node and create the host path
minikube ssh "sudo mkdir -p /mnt/data/local-pv && sudo chmod 777 /mnt/data/local-pv"
```

### Apply

```powershell
kubectl apply -f 01-local-pv.yaml
kubectl apply -f 02-local-pvc.yaml
```

### Verify

```powershell
# Check PV status — should say Available or Bound
kubectl get pv local-pv

# Check PVC status — should say Bound
kubectl get pvc local-pvc

# Full details — check "Status", "Volume", "StorageClass"
kubectl describe pv local-pv
kubectl describe pvc local-pvc
```

### Expected output

```
NAME       CAPACITY   ACCESS MODES   RECLAIM POLICY   STATUS   STORAGECLASS   AGE
local-pv   5Gi        RWO            Retain           Bound    manual-local   10s

NAME        STATUS   VOLUME     CAPACITY   ACCESS MODES   STORAGECLASS   AGE
local-pvc   Bound    local-pv   5Gi        RWO            manual-local   5s
```

---

## Step 2 — NFS PV + PVC

> Skip if no NFS server available. Use local or cloud instead.

### Setup NFS server (Linux only — run on NFS host machine)

```bash
sudo apt install nfs-kernel-server -y
sudo mkdir -p /exports/k8s-shared
sudo chmod 777 /exports/k8s-shared
echo "/exports/k8s-shared *(rw,sync,no_subtree_check,no_root_squash)" | sudo tee -a /etc/exports
sudo exportfs -a
sudo systemctl restart nfs-kernel-server
```

### Install NFS client on each K8s node

```bash
sudo apt install nfs-common -y
```

### Update NFS server IP in the YAML before applying

```powershell
# Open and edit 03-nfs-pv.yaml — change server: 192.168.1.100 to your NFS server IP
notepad 03-nfs-pv.yaml
```

### Apply

```powershell
kubectl apply -f 03-nfs-pv.yaml
kubectl apply -f 04-nfs-pvc.yaml
```

### Verify

```powershell
kubectl get pv nfs-pv
kubectl get pvc nfs-pvc
kubectl describe pvc nfs-pvc
```

---

## Step 3 — Cloud StorageClass + Dynamic PVC (AWS EBS example)

> Requires a real AWS EKS cluster with EBS CSI driver installed.

### Install AWS EBS CSI driver

```powershell
kubectl apply -k "github.com/kubernetes-sigs/aws-ebs-csi-driver/deploy/kubernetes/overlays/stable/?ref=release-1.28"

# Wait for driver pods to be ready
kubectl get pods -n kube-system -l app.kubernetes.io/name=aws-ebs-csi-driver -w
```

### Apply StorageClass

```powershell
kubectl apply -f 05-cloud-storageclass.yaml

# List all storage classes
kubectl get storageclass
kubectl describe storageclass aws-ebs-gp3
```

### Apply dynamic PVC

```powershell
kubectl apply -f 06-cloud-pvc-dynamic.yaml

# PVC stays Pending until a pod uses it (WaitForFirstConsumer mode)
kubectl get pvc cloud-pvc-dynamic
kubectl describe pvc cloud-pvc-dynamic
```

---

## Step 4 — ConfigMap + Secret

### Apply

```powershell
kubectl apply -f 07-app-configmap.yaml
kubectl apply -f 08-app-secret.yaml
```

### Verify ConfigMap

```powershell
# List all configmaps
kubectl get configmap

# View full content
kubectl describe configmap app-config

# Get raw YAML
kubectl get configmap app-config -o yaml
```

### Verify Secret

```powershell
# List secrets
kubectl get secret

# Describe (values hidden)
kubectl describe secret app-secret

# Decode a specific secret value
kubectl get secret app-secret -o jsonpath="{.data.DB_PASSWORD}" | base64 --decode
# PowerShell alternative:
$encoded = kubectl get secret app-secret -o jsonpath="{.data.DB_PASSWORD}"
[System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($encoded))
```

### Encode a new secret value (PowerShell)

```powershell
# Encode value to base64
$value = "mynewpassword"
[Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($value))
# Output: bXluZXdwYXNzd29yZA==  ← paste this into Secret YAML
```

---

## Step 5 — Deploy Application

```powershell
kubectl apply -f 09-app-deployment.yaml

# Watch pods come up
kubectl get pods -l app=myapp -w

# Check deployment status
kubectl rollout status deployment/app-deployment
```

---

## Step 6 — Test Mounts Inside Pod

```powershell
# Get a pod name
kubectl get pods -l app=myapp

# Set pod name in variable (PowerShell)
$POD = kubectl get pods -l app=myapp -o jsonpath="{.items[0].metadata.name}"
echo $POD

# Exec into pod
kubectl exec -it $POD -- sh
```

### Inside the pod shell

```sh
# Check PVC mount — persistent storage
ls /data/uploads
df -h /data/uploads

# Check ConfigMap mounted as file
cat /etc/app/app.properties

# Check Secret mounted as files
ls /etc/secrets
cat /etc/secrets/DB_USER

# Check env vars from ConfigMap
echo $APP_ENV
echo $LOG_LEVEL
echo $DB_HOST

# Check env var from Secret
echo $DB_PASSWORD

# Write test file to PVC (persists after pod restart)
echo "test data" > /data/uploads/test.txt
cat /data/uploads/test.txt

exit
```

---

## Step 7 — Test PVC Persistence

```powershell
# Write data to PVC via pod
kubectl exec $POD -- sh -c "echo 'persistent data' > /data/uploads/persist-test.txt"

# Delete the pod — deployment recreates it
kubectl delete pod $POD

# Wait for new pod
kubectl get pods -l app=myapp -w

# Get new pod name
$POD2 = kubectl get pods -l app=myapp -o jsonpath="{.items[0].metadata.name}"

# Verify data still exists in new pod
kubectl exec $POD2 -- cat /data/uploads/persist-test.txt
# Output: persistent data  ← PVC survived pod restart
```

---

## Useful Debug Commands

```powershell
# All PVs and PVCs at once
kubectl get pv,pvc

# Events — shows binding errors, provisioning failures
kubectl get events --sort-by='.lastTimestamp'

# Why is PVC stuck Pending?
kubectl describe pvc <pvc-name>

# Why is pod not starting?
kubectl describe pod <pod-name>
kubectl logs <pod-name>

# Check storage classes
kubectl get storageclass

# Check which PV a PVC is bound to
kubectl get pvc -o wide

# List all volumes in a pod spec
kubectl get pod $POD -o jsonpath="{.spec.volumes}" | ConvertFrom-Json

# Check what's mounted in a running pod
kubectl exec $POD -- mount | findstr /data
```

---

## Cleanup

```powershell
# Delete deployment first (unmounts PVC)
kubectl delete -f 09-app-deployment.yaml

# Delete PVCs (triggers PV reclaim based on reclaimPolicy)
kubectl delete -f 02-local-pvc.yaml
kubectl delete -f 04-nfs-pvc.yaml
kubectl delete -f 06-cloud-pvc-dynamic.yaml

# Delete PVs (manual — if reclaimPolicy: Retain)
kubectl delete -f 01-local-pv.yaml
kubectl delete -f 03-nfs-pv.yaml

# Delete StorageClass
kubectl delete -f 05-cloud-storageclass.yaml

# Delete ConfigMap and Secret
kubectl delete -f 07-app-configmap.yaml
kubectl delete -f 08-app-secret.yaml

# Verify all gone
kubectl get pv,pvc,configmap,secret,deployment
```

---

## Apply Everything at Once

```powershell
# From Example5 folder — apply all in filename order
kubectl apply -f .

# Or explicitly ordered
kubectl apply -f 01-local-pv.yaml `
              -f 02-local-pvc.yaml `
              -f 07-app-configmap.yaml `
              -f 08-app-secret.yaml `
              -f 09-app-deployment.yaml
```

## Delete Everything at Once

```powershell
kubectl delete -f .
```
