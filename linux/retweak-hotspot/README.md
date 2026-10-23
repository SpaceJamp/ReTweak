# retweak-hotspot

A small, read-only Linux kernel module that shows the RTX 50 hotspot to ReTweak. NVIDIA's driver
no longer reports it on Blackwell, so it's read from the GPU's thermal registers (BAR0 + 0xAD0A90).

- Reads only. It never writes to the GPU and doesn't take the GPU from the nvidia driver.
- Only attaches to GB202-GB207 (checked from the chip ID register).
- Works under Secure Boot lockdown and without `iomem=relaxed`, which block ReTweak's fallback
  (mapping the registers from user space as root).
- Any user can read `/sys/kernel/retweak_hotspot/<pci address>/channels`, so ReTweak doesn't need root for the hotspot.

## Install with DKMS (rebuilds itself after kernel updates)

Arch (and Arch-based; use the headers package for your kernel, e.g. `linux-zen-headers`):

```bash
sudo pacman -S --needed dkms linux-headers
```

Fedora (and Fedora-based):

```bash
sudo dnf install dkms kernel-devel
```

Then, from this folder:

```bash
sudo cp -r . /usr/src/retweak-hotspot-1.0
sudo dkms install retweak-hotspot/1.0
sudo modprobe retweak_hotspot
cat /sys/kernel/retweak_hotspot/*/channels
```

Load it at every boot:

```bash
echo retweak_hotspot | sudo tee /etc/modules-load.d/retweak-hotspot.conf
```

### Secure Boot (usual on Fedora)

The kernel only loads signed modules. DKMS signs with its own key (`/var/lib/dkms/mok.pub`); enroll it once,
pick a one-time password, reboot and choose "Enroll MOK" on the blue screen:

```bash
sudo mokutil --import /var/lib/dkms/mok.pub
```

## Remove

```bash
sudo modprobe -r retweak_hotspot
sudo dkms remove retweak-hotspot/1.0 --all
sudo rm -r /usr/src/retweak-hotspot-1.0 /etc/modules-load.d/retweak-hotspot.conf
```

## Without DKMS (one-off test)

```bash
make && sudo insmod retweak_hotspot.ko
```

`dmesg | grep retweak_hotspot` lists the GPUs it found, or says none is an RTX 50.
