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
 *
 * It also shows the memory-chip temperatures (whole °C, the sensors that report) in
 *     /sys/kernel/retweak_hotspot/<pci address>/memory
 * from the BAR0 + 0x900000 block, read exactly as PawnIO's NVIDIA module 0.2.11 does on Windows
 * (ioctl_read_memory_temperatures in Nvidia.p, LGPL-2.1+).
 */
#include <linux/io.h>
#include <linux/kobject.h>
#include <linux/module.h>
#include <linux/pci.h>
#include <linux/pm_runtime.h>
#include <linux/sysfs.h>
#include <linux/version.h>

/* Takes a runtime-PM reference only if the GPU is awake: 1 = awake (put it back after), 0 = asleep, <0 = runtime PM off. */
static int get_if_awake(struct device *dev)
{
#if LINUX_VERSION_CODE >= KERNEL_VERSION(6, 9, 0)
	return pm_runtime_get_if_active(dev);
#else
	return pm_runtime_get_if_active(dev, true);
#endif
}

#define CHANNEL_OFFSET 0xAD0A90
#define CHANNEL_COUNT 6
#define MAX_GPUS 8

/* Memory temperatures: 24 groups of 0x4000 from BAR0 + 0x900000, as in PawnIO's module. */
#define MEM_BASE 0x900000
#define MEM_SIZE 0x60000
#define MEM_TOPOLOGY 0x200
#define MEM_GROUP_STRIDE 0x4000
#define MEM_MASK 0x24D0
#define MEM_GROUPS 24
#define MEM_SENSORS 48
static const u32 mem_offsets[8] = { 0x24C0, 0x24C4, 0x24C8, 0x24CC, 0x1348, 0x134C, 0x1350, 0x1354 };

struct gpu {
	struct pci_dev *pdev;
	void __iomem *page;	/* the page holding the channels */
	void __iomem *mem;	/* the memory-temperature block, or NULL */
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

/*
 * Never wake a sleeping GPU (laptops) just for a temperature, and keep it from going to sleep mid-read:
 * the reference holds it awake. Runtime PM off (most desktops): the device stays in D0 unless suspended.
 * Returns what release_awake needs, or -EAGAIN when the GPU is asleep.
 */
static int hold_awake(struct gpu *g)
{
	int awake = get_if_awake(&g->pdev->dev);

	if (awake == 0 || (awake < 0 && g->pdev->current_state != PCI_D0))
		return -EAGAIN;
	return awake;
}

static void release_awake(struct gpu *g, int awake)
{
	if (awake > 0)
		pm_runtime_put(&g->pdev->dev);
}

static ssize_t channels_show(struct kobject *kobj, struct kobj_attribute *attr, char *buf)
{
	struct gpu *g = gpu_of(kobj);
	unsigned int off = CHANNEL_OFFSET & ~PAGE_MASK;
	ssize_t len = 0;
	int i, awake;

	if (!g)
		return -ENODEV;
	awake = hold_awake(g);
	if (awake == -EAGAIN)
		return -EAGAIN;
	for (i = 0; i < CHANNEL_COUNT; i++)
		len += sysfs_emit_at(buf, len, "%08x%c", readl(g->page + off + 4 * i),
				     i == CHANNEL_COUNT - 1 ? '\n' : ' ');
	release_awake(g, awake);
	return len;
}

static struct kobj_attribute channels_attr = __ATTR_RO(channels);

/* All ones, or 0xBADF in the top half: the GPU didn't answer this register. */
static bool bad_read(u32 v)
{
	return v == 0xFFFFFFFF || (v & 0xFFFF0000) == 0xBADF0000;
}

/* The memory-chip temperatures that report, in °C, space-separated (an empty line if none do). */
static ssize_t memory_show(struct kobject *kobj, struct kobj_attribute *attr, char *buf)
{
	struct gpu *g = gpu_of(kobj);
	ssize_t len = 0;
	int index, awake;
	u32 topology;

	if (!g || !g->mem)
		return -ENODEV;
	awake = hold_awake(g);
	if (awake == -EAGAIN)
		return -EAGAIN;
	topology = readl(g->mem + MEM_TOPOLOGY);
	if (bad_read(topology)) {
		release_awake(g, awake);
		return -EIO;
	}
	for (index = 0; index < MEM_SENSORS; index++) {
		int group, chip, first_input = 0, input_offset, temp = 0;
		bool have = false;
		u32 mask;

		if (topology & 0x00400000) {
			group = index >> 2;
			chip = (index >> 1) & 1;
			first_input = index & 1;
		} else {
			group = index >> 1;
			chip = index & 1;
		}
		if (group >= MEM_GROUPS)
			continue;
		mask = readl(g->mem + group * MEM_GROUP_STRIDE + MEM_MASK);
		if (bad_read(mask))
			continue;
		mask = (mask >> 24) & 0xFF;
		/* Two inputs per chip; the hotter one counts. */
		for (input_offset = 0; input_offset <= 2; input_offset += 2) {
			int sensor = chip + 2 * (first_input + input_offset), t;
			u32 raw;

			if (!(mask & (1 << sensor)))
				continue;
			raw = readl(g->mem + group * MEM_GROUP_STRIDE + mem_offsets[sensor]);
			if (bad_read(raw))
				continue;
			t = ((int)((raw >> 16) & 0xFF) - 20) * 2;
			if (!have || t > temp) {
				temp = t;
				have = true;
			}
		}
		if (have)
			len += sysfs_emit_at(buf, len, "%s%d", len ? " " : "", temp);
	}
	len += sysfs_emit_at(buf, len, "\n");
	release_awake(g, awake);
	return len;
}

static struct kobj_attribute memory_attr = __ATTR_RO(memory);

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

	bool supported;

	if (!start || pci_resource_len(pdev, 0) < CHANNEL_OFFSET + 4 * CHANNEL_COUNT)
		return;
	/* Wake the GPU once, if it's asleep (laptops), to read its chip ID; it may sleep again right after. */
	if (pm_runtime_resume_and_get(&pdev->dev) < 0)
		return;
	supported = pdev->current_state == PCI_D0 && is_supported_chip(pdev);
	pm_runtime_put(&pdev->dev);
	if (!supported)
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
	/* Memory temperatures are a bonus: without them the hotspot still works. */
	g->mem = pci_resource_len(pdev, 0) >= MEM_BASE + MEM_SIZE ? ioremap(start + MEM_BASE, MEM_SIZE) : NULL;
	if (g->mem && sysfs_create_file(g->kobj, &memory_attr.attr)) {
		iounmap(g->mem);
		g->mem = NULL;
	}
	g->pdev = pci_dev_get(pdev);
	gpu_count++;
	pr_info("retweak_hotspot: %s\n", pci_name(pdev));
}

static void remove_all(void)
{
	while (gpu_count > 0) {
		struct gpu *g = &gpus[--gpu_count];

		kobject_put(g->kobj);	/* removes the files first, so no reader is left using the mappings */
		iounmap(g->page);
		if (g->mem)
			iounmap(g->mem);
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
MODULE_DESCRIPTION("Read-only RTX 50 hotspot and memory temperatures for ReTweak");
