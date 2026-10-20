`Nvidia.bin` is the signed NVIDIA module from the official PawnIO modules
repository, release 0.2.11 (unmodified):
https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.11

SHA-256 `2a51262b7215bf3ea82c0f507bc4af50243141f82542dfe611a885c71ce2629a`

It is licensed under the LGPL 2.1 or later (see `COPYING`); source:
https://github.com/namazso/PawnIO.Modules/blob/0.2.11/Nvidia.p

ReTweak only calls its read-only `ioctl_read_thermal_registers`, which maps
24 bytes of GPU BAR0 at `0xAD0A90` on GB202-GB207 chips. The PawnIO driver itself is
not bundled; users install it from https://pawnio.eu/.
