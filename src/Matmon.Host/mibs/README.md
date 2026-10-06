# Built-in SNMP MIBs

Standard MIB modules shipped with Matmon so SNMP walks show names out of the box. Uploaded MIBs
(System → SNMP MIBs, stored in `data/mibs`) are loaded after these and replace a module of the same name.

Source: the MIB set distributed with Net-SNMP 5.9.4 (Alpine `net-snmp` package).

- IETF modules (SNMPv2-*, IF-MIB, HOST-RESOURCES-MIB, IP-MIB, ...): published in the RFCs; MIB modules are
  "Code Components" under the IETF Trust Legal Provisions and may be redistributed under the Simplified BSD License.
- IANA modules (IANAifType-MIB, IANA-*): maintained by IANA, freely redistributable.
- Net-SNMP / UCD modules (UCD-SNMP-MIB, UCD-DISKIO-MIB, NET-SNMP-*): BSD-style license of the Net-SNMP project.

This file is not a MIB; the loader ignores files without a module definition.
