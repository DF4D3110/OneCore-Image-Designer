# OCID - IDA batch decompiler.
#
# Dumps every function's Hex-Rays pseudocode (plus the disassembly listing) for one binary into
# OCID_DECOMP_OUT. Intended to be driven headlessly:
#
#   idat.exe -A -S"ocid_decompile.py" -B <binary>
#   (or: idat64.exe for 64-bit binaries)
#
# Why dumps everything instead of selected functions: the user's instruction is explicit -- read the
# decompilation completely, because a half-understood binary gets reimplemented in a way that only
# shows up much later. Disk is cheap; a wrong rewrite is not.
#
# IDA 9.x: the x86/x64 decompiler lives in plugins\hexx64.dll, so it covers 32-bit x86 as well.

import os
import sys
import traceback

import ida_auto
import ida_funcs
import ida_hexrays
import ida_nalt
import ida_segment
import ida_bytes
import idc
import ida_kernwin


def main():
    out_dir = os.environ.get("OCID_DECOMP_OUT", r"C:\ocid_decomp")
    ida_auto.auto_wait()

    binary = ida_nalt.get_root_filename() or "unknown"
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, binary + ".c")
    listing_path = os.path.join(out_dir, binary + ".asm")

    decomp_ok = False
    try:
        decomp_ok = ida_hexrays.init_hexrays_plugin()
    except Exception:
        decomp_ok = False

    n_funcs = ida_funcs.get_func_qty()
    with open(out_path, "w", encoding="utf-8", errors="replace") as f:
        f.write("// OCID decompilation dump\n")
        f.write("// binary      : %s\n" % binary)
        f.write("// input path  : %s\n" % (ida_nalt.get_input_file_path() or ""))
        f.write("// image base  : 0x%X\n" % ida_nalt.get_imagebase())
        try:
            import ida_ida
            ptr = 8 if ida_ida.inf_is_64bit() else 4
        except Exception:
            ptr = 0
        f.write("// pointer size: %d\n" % ptr)
        f.write("// functions   : %d\n" % n_funcs)
        f.write("// hexrays     : %s\n" % ("available" if decomp_ok else "NOT AVAILABLE"))
        f.write("//\n\n")

        for i in range(n_funcs):
            fn = ida_funcs.getn_func(i)
            if fn is None:
                continue
            ea = fn.start_ea
            name = idc.get_func_name(ea) or ("sub_%X" % ea)
            f.write("// " + "=" * 74 + "\n")
            f.write("// %s   @ 0x%X - 0x%X   (size 0x%X)\n" % (name, ea, fn.end_ea, fn.end_ea - ea))
            f.write("// " + "=" * 74 + "\n\n")

            if decomp_ok:
                try:
                    cf = ida_hexrays.decompile(ea)
                    if cf is not None:
                        f.write(str(cf))
                        f.write("\n\n")
                    else:
                        f.write("// [hexrays returned no function]\n\n")
                except Exception as e:
                    f.write("// [hexrays failed: %s]\n\n" % e)
            else:
                f.write("// [hexrays unavailable]\n\n")

    # Also keep a plain disassembly listing: sometimes the pseudocode hides a calling convention
    # or a struct offset that the raw code makes obvious.
    try:
        with open(listing_path, "w", encoding="utf-8", errors="replace") as g:
            for i in range(n_funcs):
                fn = ida_funcs.getn_func(i)
                if fn is None:
                    continue
                ea = fn.start_ea
                g.write("; %s @ 0x%X\n" % (idc.get_func_name(ea), ea))
                cur = ea
                while cur < fn.end_ea:
                    g.write("  %08X  %s\n" % (cur, idc.generate_disasm_line(cur, 0) or ""))
                    nxt = idc.next_head(cur, fn.end_ea)
                    if nxt <= cur:
                        break
                    cur = nxt
                g.write("\n")
    except Exception:
        pass

    ida_kernwin.msg("OCID: wrote %s\n" % out_path)
    idc.qexit(0)


try:
    main()
except Exception:
    with open(os.path.join(os.environ.get("OCID_DECOMP_OUT", r"C:\ocid_decomp"), "_script_error.txt"), "a") as e:
        e.write(traceback.format_exc())
    idc.qexit(1)
