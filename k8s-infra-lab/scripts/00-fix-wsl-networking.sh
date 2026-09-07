#!/bin/bash
# Run as root inside the WSL2 Ubuntu distro.
# Fixes two WSL2-specific gaps that block Podman's container networking.
set -ex

# 1. minikube's podman driver bind-mounts /lib/modules into the node container.
#    WSL2's kernel has no real /lib/modules tree (its features are built-in,
#    not loaded as .ko files), so the mount source doesn't exist. A stub
#    directory is enough to satisfy it.
mkdir -p "/lib/modules/$(uname -r)"

# 2. Podman's default firewall path uses the iptables-nft compat shim, which
#    fails to append NAT rules on this WSL2 kernel build ("RULE_APPEND
#    failed: No such file or directory") because the kernel's nf_tables NAT
#    support is incomplete. The kernel's legacy xtables NAT path works fine,
#    so switch the system alternatives to it.
update-alternatives --set iptables /usr/sbin/iptables-legacy
update-alternatives --set ip6tables /usr/sbin/ip6tables-legacy
