// SPDX-License-Identifier: GPL-2.0
/*
 * retweak_hotspot: the RTX 50 (Blackwell) hotspot for ReTweak, read-only.
 *
 * NVIDIA's driver no longer reports the hotspot on RTX 50. It sits in six thermal registers at
 * BAR0 + 0xAD0A90 (see ReTweak's docs/power-detector.md, "Hotspot"). This module maps just that
 * page of each GB202-GB207 GPU and shows the raw registers in
 *     /sys/kernel/retweak_hotspot/<pci address>/channels
 * as six hex values, readable by any user. It never writes to the GPU, doesn't bind to it
 * (the nvidia driver keeps it) and doesn't need iomem=relaxed.
 */
#include <linux/io.h>
#include <linux/kobject.h>
#include <linux/module.h>
#include <linux/pci.h>
#include <linux/slab.h>
#include <linux/sysfs.h>

#define CHANNEL_OFFSET 0xAD0A90
#define CHANNEL_COUNT 6
#define MAX_GPUS 8

struct gpu {
	struct pci_dev *pdev;
	void __iomem *page;	/* the page holding the channels */
	struct kobject *kobj;
};

static struct kobject *root;
static struct gpu gpus[MAX_GPUS];
static int gpu_count;

static struct gpu *gpu_of(struct kobject *kobj)
{
	int i;

	for (i = 0; i < gpu_count; i++)
		if (gpus[i].kobj == kobj)
			return &gpus[i];
	return NULL;
}

static ssize_t channels_show(struct kobject *kobj, struct kobj_attribute *attr, char *buf)
{
	struct gpu *g = gpu_of(kobj);
	unsigned int off = CHANNEL_OFFSET & ~PAGE_MASK;
	ssize_t len = 0;
	int i;

	if (!g)
		return -ENODEV;
	/* A powered-down GPU answers all ones; don't wake it, just say it's not there right now. */
	if (g->pdev->current_state != PCI_D0)
		return -EAGAIN;
	for (i = 0; i < CHANNEL_COUNT; i++)
		len += sysfs_emit_at(buf, len, "%08x%c", readl(g->page + off + 4 * i),
				     i == CHANNEL_COUNT - 1 ? '\n' : ' ');
	return len;
}

static struct kobj_attribute channels_attr = __ATTR_RO(channels);

/* PMC_BOOT_0 (BAR0 + 0) bits 28:20 are the chip; 0x1b2-0x1b7 are GB202-GB207, the only chips with these registers. */
static bool is_supported_chip(struct pci_dev *pdev)
{
	void __iomem *boot;
	u32 chip;

	boot = ioremap(pci_resource_start(pdev, 0), 4);
	if (!boot)
		return false;
	chip = (readl(boot) >> 20) & 0x1ff;
	iounmap(boot);
	return chip >= 0x1b2 && chip <= 0x1b7;
}

static void add_gpu(struct pci_dev *pdev)
{
	struct gpu *g = &gpus[gpu_count];
	resource_size_t start = pci_resource_start(pdev, 0);

	if (!start || pci_resource_len(pdev, 0) < CHANNEL_OFFSET + 4 * CHANNEL_COUNT)
		return;
	if (pdev->current_state != PCI_D0 || !is_supported_chip(pdev))
		return;
	g->page = ioremap(start + (CHANNEL_OFFSET & PAGE_MASK), PAGE_SIZE);
	if (!g->page)
		return;
	g->kobj = kobject_create_and_add(pci_name(pdev), root);
	if (!g->kobj || sysfs_create_file(g->kobj, &channels_attr.attr)) {
		kobject_put(g->kobj);
		iounmap(g->page);
		return;
	}
	g->pdev = pci_dev_get(pdev);
	gpu_count++;
	pr_info("retweak_hotspot: %s\n", pci_name(pdev));
}

static void remove_all(void)
{
	while (gpu_count > 0) {
		struct gpu *g = &gpus[--gpu_count];

		kobject_put(g->kobj);	/* removes the file first, so no reader is left using the mapping */
		iounmap(g->page);
		pci_dev_put(g->pdev);
	}
	kobject_put(root);
}

static int __init retweak_hotspot_init(void)
{
	struct pci_dev *pdev = NULL;

	root = kobject_create_and_add("retweak_hotspot", kernel_kobj);
	if (!root)
		return -ENOMEM;
	/* pci_get_device drops the reference to the previous device as it goes. */
	while (gpu_count < MAX_GPUS && (pdev = pci_get_device(PCI_VENDOR_ID_NVIDIA, PCI_ANY_ID, pdev)))
		if ((pdev->class >> 16) == PCI_BASE_CLASS_DISPLAY)
			add_gpu(pdev);
	pci_dev_put(pdev);
	if (gpu_count == 0) {
		remove_all();
		pr_info("retweak_hotspot: no RTX 50 GPU found\n");
		return -ENODEV;
	}
	return 0;
}

static void __exit retweak_hotspot_exit(void)
{
	remove_all();
}

module_init(retweak_hotspot_init);
module_exit(retweak_hotspot_exit);
MODULE_LICENSE("GPL");
MODULE_DESCRIPTION("Read-only RTX 50 hotspot registers for ReTweak");
