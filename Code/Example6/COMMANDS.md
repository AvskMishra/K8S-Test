# Example 6 — Quick Command Reference

## Deploy Everything

```bash
kubectl apply -f 02-mysql-secret.yaml
kubectl apply -f 03-mysql-configmap.yaml
kubectl apply -f 04-mysql-headless-service.yaml
kubectl apply -f 05-mysql-statefulset.yaml
kubectl rollout status statefulset/mysql
kubectl apply -f 01-dotnet-deployment.yaml
```

## StatefulSet Inspection

```bash
# List pods — notice ordered names: mysql-0, mysql-1, mysql-2
kubectl get pods -l app=mysql

# Watch ordered startup live
kubectl get pods -l app=mysql -w

# Describe StatefulSet
kubectl describe statefulset mysql

# Check PVCs — one per pod
kubectl get pvc
# Expected:
# data-mysql-0   Bound
# data-mysql-1   Bound
# data-mysql-2   Bound

# Check stable DNS resolution
kubectl exec mysql-0 -- nslookup mysql-0.mysql
kubectl exec mysql-1 -- nslookup mysql-0.mysql   # always resolves to master
```

## MySQL Replication Verification

```bash
# Master: check binary log is active
kubectl exec mysql-0 -- mysql -uroot -prootpassword -e "SHOW MASTER STATUS\G"

# Replica: check replication running
kubectl exec mysql-1 -- mysql -uroot -prootpassword -e "SHOW SLAVE STATUS\G"
kubectl exec mysql-2 -- mysql -uroot -prootpassword -e "SHOW SLAVE STATUS\G"

# Test write on master → read on replica
kubectl exec mysql-0 -- mysql -uroot -prootpassword -e "CREATE DATABASE IF NOT EXISTS testdb; USE testdb; CREATE TABLE IF NOT EXISTS test (id INT); INSERT INTO test VALUES (1);"
kubectl exec mysql-1 -- mysql -uroot -prootpassword -e "USE testdb; SELECT * FROM test;"
```

## Scaling

```bash
# Scale stateless .NET app — trivial, no ordering
kubectl scale deployment dotnet-app --replicas=5
kubectl scale deployment dotnet-app --replicas=2   # any 3 pods deleted

# Scale StatefulSet UP — mysql-3 starts, clones from mysql-2
kubectl scale statefulset mysql --replicas=4

# Scale StatefulSet DOWN — mysql-3 deleted first, master safe
kubectl scale statefulset mysql --replicas=2
```

## Pod Identity Proof

```bash
# Delete mysql-1 — it will come back with SAME name and SAME PVC
kubectl delete pod mysql-1
kubectl get pods -l app=mysql -w
# mysql-1 re-appears, rebinds data-mysql-1
```

## Logs

```bash
kubectl logs mysql-0 -c init-mysql     # init container log
kubectl logs mysql-0 -c clone-mysql    # clone container log
kubectl logs mysql-0 -c mysql          # main MySQL log
kubectl logs mysql-0 -c xtrabackup     # sidecar log
```

## Cleanup

```bash
kubectl delete deployment dotnet-app
kubectl delete statefulset mysql
kubectl delete service mysql mysql-read dotnet-app-service
kubectl delete secret mysql-secret
kubectl delete configmap mysql-init-config
# PVCs are NOT deleted automatically — intentional, data preserved
kubectl delete pvc data-mysql-0 data-mysql-1 data-mysql-2
```
