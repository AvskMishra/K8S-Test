#!/bin/bash
set -ex
kubectl apply -f /mnt/c/CodeBase/app/k8s/00-namespace.yaml
kubectl apply -f /mnt/c/CodeBase/app/k8s/10-mongo.yaml
kubectl apply -f /mnt/c/CodeBase/app/k8s/20-api.yaml
kubectl apply -f /mnt/c/CodeBase/app/k8s/30-frontend.yaml

echo "--- waiting for rollouts ---"
kubectl -n product-catalog rollout status deployment/mongo --timeout=120s
kubectl -n product-catalog rollout status deployment/product-api --timeout=120s
kubectl -n product-catalog rollout status deployment/product-frontend --timeout=120s

echo "--- pods, spread across nodes ---"
kubectl -n product-catalog get pods -o wide
