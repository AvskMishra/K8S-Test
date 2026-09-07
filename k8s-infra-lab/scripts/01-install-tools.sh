#!/bin/bash
# Run as root inside the WSL2 Ubuntu distro.
# Installs Podman, minikube, and kubectl.
set -ex

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq podman uidmap curl conntrack

cd /tmp
curl -Lo minikube https://storage.googleapis.com/minikube/releases/latest/minikube-linux-amd64
install minikube /usr/local/bin/minikube
rm -f minikube

KVER=$(curl -Ls https://dl.k8s.io/release/stable.txt)
curl -Lo kubectl "https://dl.k8s.io/release/${KVER}/bin/linux/amd64/kubectl"
install kubectl /usr/local/bin/kubectl
rm -f kubectl

minikube version
kubectl version --client
