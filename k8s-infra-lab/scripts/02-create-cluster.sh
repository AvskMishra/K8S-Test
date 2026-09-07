#!/bin/bash
# Run as root inside the WSL2 Ubuntu distro.
# Creates the 3-node (1 control-plane + 2 worker) minikube cluster.
#
# NOTE on --force: minikube refuses the podman driver as root by default
# (it recommends rootless podman for isolation). We tried rootless first,
# but the CRI-O variant of minikube's kicbase image fails to boot under
# rootless podman (its init script tries to enable a
# containerd-fuse-overlayfs systemd unit that isn't present in that image
# variant). Running as root inside this throwaway WSL2 Ubuntu distro is an
# acceptable tradeoff for a local learning cluster. --force overrides the
# safety check.
set -ex

minikube start -p k8slab \
  --nodes=3 \
  --driver=podman \
  --container-runtime=crio \
  --cpus=2 \
  --memory=2500mb \
  --disk-size=15g \
  --force

kubectl get nodes -o wide
kubectl get pods -A
