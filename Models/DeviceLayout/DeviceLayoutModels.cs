using System.Collections.Generic;
using System.Xml.Serialization;

namespace OneCore_Image_Designer.Models.DeviceLayout
{
    /// <summary>
    /// DeviceLayout.xml 根元素 (支持三种格式)
    /// v1: 直接包含 Partitions
    /// v2a: 直接包含 Stores (无StoragePools层)
    /// v2b: 包含 StoragePools -> StoragePool -> Stores
    /// </summary>
    [XmlRoot("DeviceLayout", Namespace = "http://schemas.microsoft.com/embedded/2004/10/ImageUpdate")]
    public class DeviceLayoutDoc
    {
        // v1格式: 直接分区列表
        [XmlArray("Partitions")]
        [XmlArrayItem("Partition")]
        public List<Partition> Partitions { get; set; } = new();
        public bool ShouldSerializePartitions() => Partitions.Count > 0;

        // v2a格式: 直接Stores (无StoragePools层)
        [XmlArray("Stores")]
        [XmlArrayItem("Store")]
        public List<Store> Stores { get; set; } = new();
        public bool ShouldSerializeStores() => Stores.Count > 0;

        // v2b格式: 存储池
        [XmlArray("StoragePools")]
        [XmlArrayItem("StoragePool")]
        public List<StoragePool> StoragePools { get; set; } = new();
        public bool ShouldSerializeStoragePools() => StoragePools.Count > 0;

        [XmlElement("SectorSize")]
        public int SectorSize { get; set; } = 512;

        [XmlElement("ChunkSize")]
        public int ChunkSize { get; set; }

        [XmlElement("DefaultPartitionByteAlignment")]
        public string DefaultPartitionByteAlignment { get; set; } = string.Empty;

        /// <summary>
        /// 源CAB文件路径
        /// </summary>
        [XmlIgnore]
        public string SourceCabPath { get; set; } = string.Empty;

        /// <summary>
        /// 是否为字节制布局（SpaceSplit 的 MSDeviceLayout：Store/Partition 用 SizeInBytes/TotalBytes）。
        /// 由解析层根据根标签与字段检测设置；打包时据此决定文件名（DeviceLayoutMS.xml）与序列化形状。
        /// </summary>
        [XmlIgnore]
        public bool UsesBytes { get; set; }

        /// <summary>根元素标签：DeviceLayout 或 MSDeviceLayout。</summary>
        [XmlIgnore]
        public string RootTag { get; set; } = "DeviceLayout";

        /// <summary>本布局是否含存储池。</summary>
        [XmlIgnore]
        public bool HasStoragePool => StoragePools.Count > 0;

        /// <summary>
        /// 反序列化后调用：给每个分区打「归属」标记——在哪个存储池（池内）/
        /// 在哪个 Store（池外的物理盘或 LUN）。分区表界面用 LocationTag 区分显示，
        /// 避免池内成员与池外容器分区混淆。
        /// </summary>
        public void DecorateProvenance()
        {
            foreach (Partition p in Partitions)
            {
                p.InStoragePool = false; p.OwnerPool = ""; p.OwnerStoreType = "";
            }
            foreach (Store s in Stores)
            {
                foreach (Partition p in s.Partitions)
                {
                    p.InStoragePool = false; p.OwnerPool = "";
                    p.OwnerStoreType = string.IsNullOrWhiteSpace(s.StoreType) ? "(未命名盘)" : s.StoreType;
                }
            }
            foreach (StoragePool pool in StoragePools)
            {
                foreach (Store s in pool.Stores)
                {
                    foreach (Partition p in s.Partitions)
                    {
                        p.InStoragePool = true;
                        p.OwnerPool = string.IsNullOrWhiteSpace(pool.Name) ? "(未命名池)" : pool.Name;
                        p.OwnerStoreType = string.IsNullOrWhiteSpace(s.StoreType) ? "(未命名盘)" : s.StoreType;
                    }
                }
            }
        }

        /// <summary>
        /// 反序列化后调用：把根节点的 <see cref="SectorSize"/> 向下传播到所有
        /// <see cref="Store"/> 与 <see cref="Partition"/>。
        ///
        /// 必要性：SectorSize 只存在于根节点，而 Store/Partition 的容量换算需要它。
        /// 原实现直接在属性里硬编码 ×512（注释写着"暂时用512，后续可从父级获取"），
        /// 导致 4096 字节扇区的布局显示容量只有真实值的 1/8 —— 例如 4K 扇区下
        /// 一个 2097152 扇区的分区，真实是 8 GB，却显示为 1 GB。
        ///
        /// 根节点 SectorSize 非法（&lt;=0）时统一回退为 512。
        /// </summary>
        public void ApplySectorSize()
        {
            int sector = SectorSize > 0 ? SectorSize : 512;
            SectorSize = sector;

            foreach (Partition p in Partitions)
                p.SectorSize = sector;

            foreach (Store s in Stores)
                PropagateToStore(s, sector);

            foreach (StoragePool pool in StoragePools)
            {
                foreach (Store s in pool.Stores)
                    PropagateToStore(s, sector);
            }
        }

        private static void PropagateToStore(Store store, int sector)
        {
            store.SectorSize = sector;
            foreach (Partition p in store.Partitions)
                p.SectorSize = sector;
        }

        /// <summary>
        /// 获取所有分区（合并所有格式）
        /// 注意：v1格式的Partitions会通过AllStores的虚拟Store暴露，这里避免重复计算
        /// </summary>
        [XmlIgnore]
        public List<Partition> AllPartitions
        {
            get
            {
                var all = new List<Partition>();

                // 如果有Stores或StoragePools（v2格式），从Stores中获取
                if (Stores.Count > 0 || StoragePools.Count > 0)
                {
                    foreach (var store in AllStores)
                    {
                        all.AddRange(store.Partitions);
                    }
                }
                else
                {
                    // v1格式：直接Partitions
                    all.AddRange(Partitions);
                }

                return all;
            }
        }

        /// <summary>
        /// 获取所有Store（用于UI展示）
        /// 支持三种格式：v1直接Partitions、v2a直接Stores、v2b StoragePools
        /// </summary>
        [XmlIgnore]
        public List<Store> AllStores
        {
            get
            {
                var stores = new List<Store>();

                // v2a: 直接Stores
                stores.AddRange(Stores);

                // v2b: StoragePools -> StoragePool -> Stores
                foreach (var pool in StoragePools)
                {
                    stores.AddRange(pool.Stores);
                }

                // v1: 直接Partitions -> 创建虚拟Store
                if (Partitions.Count > 0 && stores.Count == 0)
                {
                    stores.Add(new Store
                    {
                        StoreType = "MainOSDisk",
                        Partitions = new List<Partition>(Partitions),
                        SizeInSectors = Partitions.Sum(p => p.TotalSectors),
                        // 虚拟 Store 不在 XML 中，须继承根节点扇区大小，
                        // 否则其容量会按默认 512 计算而与分区不一致。
                        SectorSize = SectorSize > 0 ? SectorSize : 512
                    });
                }

                return stores;
            }
        }
    }

    public class StoragePool
    {
        [XmlElement("Name")]
        public string Name { get; set; } = string.Empty;
        public bool ShouldSerializeName() => !string.IsNullOrEmpty(Name);

        [XmlArray("Stores")]
        [XmlArrayItem("Store")]
        public List<Store> Stores { get; set; } = new();
        public bool ShouldSerializeStores() => Stores.Count > 0;
    }

    public class Store
    {
        [XmlElement("Id")]
        public string Id { get; set; } = string.Empty;
        public bool ShouldSerializeId() => !string.IsNullOrEmpty(Id);

        [XmlElement("StoreType")]
        public string StoreType { get; set; } = string.Empty;
        public bool ShouldSerializeStoreType() => !string.IsNullOrEmpty(StoreType);

        [XmlElement("DevicePath")]
        public string DevicePath { get; set; } = string.Empty;
        public bool ShouldSerializeDevicePath() => !string.IsNullOrEmpty(DevicePath);

        [XmlElement("SizeInSectors")]
        public long SizeInSectors { get; set; }
        public bool ShouldSerializeSizeInSectors() => SizeInSectors != 0;

        /// <summary>字节制布局（SpaceSplit 的 MSDeviceLayout）用 SizeInBytes 而非 SizeInSectors。</summary>
        [XmlElement("SizeInBytes")]
        public long SizeInBytesXml { get; set; }
        public bool ShouldSerializeSizeInBytesXml() => SizeInBytesXml != 0;

        [XmlElement("OnlyAllocateDefinedGptEntries")]
        public bool OnlyAllocateDefinedGptEntries { get; set; }
        public bool ShouldSerializeOnlyAllocateDefinedGptEntries() => OnlyAllocateDefinedGptEntries;

        [XmlArray("Partitions")]
        [XmlArrayItem("Partition")]
        public List<Partition> Partitions { get; set; } = new();
        public bool ShouldSerializePartitions() => Partitions.Count > 0;

        /// <summary>
        /// 扇区大小（字节）。不来自 XML，由父级 <see cref="DeviceLayoutDoc.SectorSize"/>
        /// 在反序列化后通过 <see cref="DeviceLayoutDoc.ApplySectorSize"/> 向下传播。
        /// </summary>
        [XmlIgnore]
        public int SectorSize { get; set; } = 512;

        /// <summary>
        /// 总大小（字节）。字节制布局（SizeInBytesXml&gt;0）直接取之，
        /// 否则按 SizeInSectors × 扇区大小换算。
        /// </summary>
        [XmlIgnore]
        public long SizeInBytes => SizeInBytesXml > 0
            ? SizeInBytesXml
            : SizeInSectors * (long)EffectiveSectorSize;

        /// <summary>扇区大小，非法值时回退 512。</summary>
        [XmlIgnore]
        public int EffectiveSectorSize => SectorSize > 0 ? SectorSize : 512;

        /// <summary>
        /// 总大小（MB）
        /// </summary>
        [XmlIgnore]
        public double SizeInMB => SizeInBytes / (1024.0 * 1024.0);

        /// <summary>
        /// 总大小（GB）
        /// </summary>
        [XmlIgnore]
        public double SizeInGB => SizeInMB / 1024.0;

        /// <summary>
        /// 显示大小
        /// </summary>
        [XmlIgnore]
        public string DisplaySize
        {
            get
            {
                if (SizeInBytes == 0)
                    return "动态";
                if (SizeInGB >= 1)
                    return $"{SizeInGB:F2} GB";
                if (SizeInMB >= 1)
                    return $"{SizeInMB:F1} MB";
                return $"{SizeInBytes} B";
            }
        }
    }

    public class Partition
    {
        [XmlElement("Name")]
        public string Name { get; set; } = string.Empty;

        [XmlElement("Type")]
        public string Type { get; set; } = string.Empty;

        [XmlElement("FileSystem")]
        public string FileSystem { get; set; } = string.Empty;
        public bool ShouldSerializeFileSystem() => !string.IsNullOrEmpty(FileSystem);

        [XmlElement("TotalSectors")]
        public long TotalSectors { get; set; }
        public bool ShouldSerializeTotalSectors() => TotalSectors != 0;

        [XmlElement("MinFreeSectors")]
        public long MinFreeSectors { get; set; }
        public bool ShouldSerializeMinFreeSectors() => MinFreeSectors != 0;

        /// <summary>
        /// 字节制布局（SpaceSplit 的 MSDeviceLayout）用 TotalBytes 而非 TotalSectors。
        /// 解析/显示/打包在 UsesBytes 模式下以此为准。
        /// </summary>
        [XmlElement("TotalBytes")]
        public long TotalBytes { get; set; }
        public bool ShouldSerializeTotalBytes() => TotalBytes != 0;

        [XmlElement("UseAllSpace")]
        public bool UseAllSpace { get; set; }
        public bool ShouldSerializeUseAllSpace() => UseAllSpace;

        [XmlElement("ByteAlignment")]
        public string ByteAlignment { get; set; } = string.Empty;
        public bool ShouldSerializeByteAlignment() => !string.IsNullOrEmpty(ByteAlignment);

        [XmlElement("ClusterSize")]
        public string ClusterSize { get; set; } = string.Empty;
        public bool ShouldSerializeClusterSize() => !string.IsNullOrEmpty(ClusterSize);

        [XmlElement("AttachDriveLetter")]
        public bool AttachDriveLetter { get; set; }
        public bool ShouldSerializeAttachDriveLetter() => AttachDriveLetter;

        [XmlElement("PrepareFveMetadata")]
        public bool PrepareFveMetadata { get; set; }
        public bool ShouldSerializePrepareFveMetadata() => PrepareFveMetadata;

        [XmlElement("Bootable")]
        public bool Bootable { get; set; }
        public bool ShouldSerializeBootable() => Bootable;

        [XmlElement("RequiredToFlash")]
        public bool RequiredToFlash { get; set; }
        public bool ShouldSerializeRequiredToFlash() => RequiredToFlash;

        /// <summary>
        /// 扇区大小（字节）。不来自 XML，由父级 <see cref="DeviceLayoutDoc.SectorSize"/>
        /// 在反序列化后通过 <see cref="DeviceLayoutDoc.ApplySectorSize"/> 向下传播。
        /// </summary>
        [XmlIgnore]
        public int SectorSize { get; set; } = 512;

        /// <summary>扇区大小，非法值时回退 512。</summary>
        [XmlIgnore]
        public int EffectiveSectorSize => SectorSize > 0 ? SectorSize : 512;

        /// <summary>
        /// 总大小（字节）。字节制布局（SpaceSplit MSDeviceLayout，TotalBytes&gt;0）直接取之，
        /// 否则按 TotalSectors × 扇区大小换算。
        /// </summary>
        [XmlIgnore]
        public long SizeInBytes => TotalBytes > 0 ? TotalBytes : TotalSectors * (long)EffectiveSectorSize;

        /// <summary>总大小（MB）</summary>
        [XmlIgnore]
        public double SizeInMB => SizeInBytes / (1024.0 * 1024.0);

        /// <summary>总大小（GB）</summary>
        [XmlIgnore]
        public double SizeInGB => SizeInMB / 1024.0;

        // ── 归属（不来自本节点 XML，由 DeviceLayoutDoc.DecorateProvenance 填充）──
        // 用途：让分区表界面一眼分清某分区属于「存储池内虚拟盘」还是「池外物理盘/LUN」，
        //      避免把池内成员与池外容器分区（如 Default 盘上的 OSPool 容器）混为一谈。
        [XmlIgnore] public bool InStoragePool { get; set; }
        [XmlIgnore] public string OwnerPool { get; set; } = string.Empty;
        [XmlIgnore] public string OwnerStoreType { get; set; } = string.Empty;

        /// <summary>界面「位置」列：存储池内 / 池外·物理盘或LUN / 单盘。</summary>
        [XmlIgnore]
        public string LocationTag =>
            InStoragePool ? $"池内 · {OwnerStoreType}"
            : (string.IsNullOrEmpty(OwnerStoreType) ? "单盘" : $"池外 · {OwnerStoreType}");

        /// <summary>
        /// 显示大小（自动选择单位，兼容扇区制与字节制）
        /// </summary>
        [XmlIgnore]
        public string DisplaySize
        {
            get
            {
                if (UseAllSpace)
                    return "剩余空间";
                if (SizeInGB >= 1)
                    return $"{SizeInGB:F2} GB";
                if (SizeInMB >= 1)
                    return $"{SizeInMB:F1} MB";
                if (TotalSectors > 0 || TotalBytes > 0)
                    return $"{SizeInBytes} B";
                return MinFreeSectors > 0
                    ? $"{MinFreeSectors * (long)EffectiveSectorSize / (1024.0 * 1024.0):F1} MB (最小)"
                    : "动态";
            }
        }
    }
}
