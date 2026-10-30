# Built-in SNMP MIBs

Standard MIB modules shipped with Matmon so SNMP walks show names out of the box. Uploaded MIBs
(System → SNMP MIBs, stored in `data/mibs`) are loaded after these and replace a module of the same name.

Source: the MIB set distributed with Net-SNMP 5.9.4 (Alpine `net-snmp` package), plus UPS-MIB (RFC 1628) from the
libsmi MIB collection (Alpine `libsmi` package) - UPS values and the UPS traps (upsTrapOnBattery, ...) get their names.

Since 2026-10-30 also the modules switch vendors' MIBs import (their "Missing dependencies" otherwise): from
libsmi P-BRIDGE-MIB, Q-BRIDGE-MIB, RMON2-MIB, TOKEN-RING-RMON-MIB, RFC1271-MIB, ENTITY-MIB, ENTITY-SENSOR-MIB,
ENTITY-STATE-MIB, ENTITY-STATE-TC-MIB, IANA-ENTITY-MIB, UUID-TC-MIB, POWER-ETHERNET-MIB, MAU-MIB, IANA-MAU-MIB; from
Net-SNMP RMON-MIB and SNMP-TARGET/-NOTIFICATION/-USER-BASED-SM/-VIEW-BASED-ACM/-MPD-MIB; and from the IEEE 802.1
MIB repository (https://www.ieee802.org/1/files/public/MIBs/) LLDP-MIB, LLDP-EXT-DOT1-MIB, LLDP-EXT-DOT3-MIB
(802.1AB-2005), LLDP-V2-MIB, LLDP-V2-TC-MIB (802.1AB-2016) and IEEE8021-PAE-MIB (802.1X-2004). `ShippedMibTests`
fails when a shipped module imports one that is not shipped.

- IEEE 802.1 modules: published by the IEEE 802.1 working group for free download and use in implementations;
  copyright IEEE, see the respective standard for the full legal notices.
- IETF modules (SNMPv2-*, IF-MIB, HOST-RESOURCES-MIB, IP-MIB, ...): published in the RFCs; MIB modules are
  "Code Components" under the IETF Trust Legal Provisions and may be redistributed under the Simplified BSD License.
- IANA modules (IANAifType-MIB, IANA-*): maintained by IANA, freely redistributable.
- Net-SNMP / UCD modules (UCD-SNMP-MIB, UCD-DISKIO-MIB, NET-SNMP-*): BSD-style license of the Net-SNMP project.

This file is not a MIB; the loader ignores files without a module definition.
