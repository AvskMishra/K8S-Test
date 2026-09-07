#!/bin/bash
# `minikube image load` isn't resolving local images correctly in this
# environment. Instead, transfer each image directly into every node
# container's own podman storage — CRI-O and podman share the same
# underlying containers/storage backend inside the kicbase image, so an
# image loaded via `podman load` there becomes visible to CRI-O too.
set -ex

NODES="k8slab k8slab-m02 k8slab-m03"
IMAGES="product-api:dev product-frontend:dev"

for img in $IMAGES; do
  for node in $NODES; do
    podman save "localhost/$img" | podman exec -i "$node" podman load
  done
done

echo "--- verifying on each node via crictl ---"
for node in $NODES; do
  echo "== $node =="
  podman exec "$node" crictl images | grep product || echo "(not found)"
done
