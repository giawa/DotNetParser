﻿using LibDotNetParser;
using LibDotNetParser.CILApi;
using LibDotNetParser.CILApi.IL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace libDotNetClr
{
    /// <summary>
    /// .NET code executor
    /// </summary>
    public partial class DotNetClr
    {
        private DotNetFile file;
        private string EXEPath;
        /// <summary>
        /// Is the CLR running?
        /// </summary>
        private bool Running = false;
        /// <summary>
        /// Loaded DLLS
        /// </summary>
        private Dictionary<string, DotNetFile> dlls = new Dictionary<string, DotNetFile>();
        /// <summary>
        /// Callstack
        /// </summary>
        private List<CallStackItem> CallStack = new List<CallStackItem>();
        /// <summary>
        /// List of custom internal methods
        /// </summary>
        private Dictionary<string, ClrCustomInternalMethod> CustomInternalMethods = new Dictionary<string, ClrCustomInternalMethod>();
        /// <summary>
        /// Should debug messages be printed to the console?
        /// </summary>
        public bool ShouldPrintDebugMessages { get; set; }
        public DotNetClr(DotNetFile exe, string DllPath)
        {
            if (!Directory.Exists(DllPath))
            {
                throw new DirectoryNotFoundException(DllPath);
            }
            if (exe == null)
            {
                throw new ArgumentException(nameof(exe));
            }

            EXEPath = DllPath;
            Init(exe);
        }
        private void Init(DotNetFile p)
        {
            file = p;
            dlls.Clear();
            dlls.Add("main_exe", p);

            RegisterAllInternalMethods();
        }


        /// <summary>
        /// Runs the entry point
        /// </summary>
        /// <param name="args">String array of arguments</param>
        public void Start(string[] args = null)
        {
            try
            {
                if (file.EntryPoint == null)
                {
                    clrError("The entry point was not found.", "System.EntryPointNotFoundException");
                    file = null;
                    return;
                }
            }
            catch (Exception x)
            {
                clrError("The entry point was not found. Internal error: " + x.Message, "System.EntryPointNotFoundException");
                file = null;
                return;
            }
            InitAssembly(file, true);
            if (ShouldPrintDebugMessages)
            {
                Console.WriteLine();
                PrintColor("Jumping to entry point", ConsoleColor.DarkMagenta);
            }

            Running = true;

            //Run the entry point
            var Startparams = new CustomList<MethodArgStack>();
            if (args != null)
            {
                MethodArgStack[] itms = new MethodArgStack[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    itms[i] = MethodArgStack.String(args[i]);
                }

                var array = Arrays.AllocArray(itms.Length);
                array.Items = itms;
                Startparams.Add(new MethodArgStack() { type = StackItemType.Array, value = array.Index });
            }
            CallStack.Clear();
            RunMethod(file.EntryPoint, file, Startparams);
        }
        private void InitAssembly(DotNetFile file, bool InitCorLib)
        {
            if (InitCorLib)
                ResolveDLL("mscorlib"); //Always resolve mscorlib, incase the exe uses .net core

            //Resolve all of the DLLS
            foreach (var item in file.Backend.Tabels.AssemblyRefTabel)
            {
                var fileName = file.Backend.ClrStringsStream.GetByOffset(item.Name);
                ResolveDLL(fileName);
            }
            Running = true;

            //Call all static contructors
            foreach (var t in file.Types)
            {
                foreach (var m in t.Methods)
                {
                    if (m.Name == ".cctor" && m.IsStatic)
                    {
                        if (ShouldPrintDebugMessages)
                        {
                            Console.WriteLine("Creating " + t.FullName + "." + m.Name);
                        }

                        RunMethod(m, file, new CustomList<MethodArgStack>(), false);
                    }
                }
            }
        }
        private void ResolveDLL(string fileName)
        {
            string fullPath = "";
            if (dlls.ContainsKey(fileName))
            {
                //already loaded
                return;
            }

            if (File.Exists(Path.Combine(EXEPath, fileName + ".exe")))
            {
                fullPath = Path.Combine(EXEPath, fileName + ".exe");
            }
            else if (File.Exists(Path.Combine(EXEPath, fileName + ".dll")))
            {
                fullPath = Path.Combine(EXEPath, fileName + ".dll");
            }
            else if (File.Exists(fileName + ".exe"))
            {
                fullPath = fileName + ".exe";
            }
            else if (File.Exists(fileName + ".dll"))
            {
                fullPath = fileName + ".dll";
            }


            if (!string.IsNullOrEmpty(fullPath))
            {
                var file2 = new DotNetFile(fullPath);
                dlls.Add(fileName, file2);
                InitAssembly(file2, false);
                PrintColor("[OK] Loaded assembly: " + fileName, ConsoleColor.Green);
            }
            else
            {
                if (!fileName.StartsWith("System") && fileName != "netstandard")
                    PrintColor("[ERROR] Load failed: " + fileName, ConsoleColor.Red);
            }
        }
        /// <summary>
        /// Runs a method
        /// </summary>
        /// <param name="m">The method</param>
        /// <param name="file">The file of the method</param>
        /// <param name="oldStack">Old stack</param>
        /// <returns>Returns the return value</returns>
        private MethodArgStack RunMethod(DotNetMethod m, DotNetFile file, CustomList<MethodArgStack> parms, bool addToCallStack = true)
        {
            CustomList<MethodArgStack> stack = new CustomList<MethodArgStack>();
            if (m.Name == ".ctor" && m.Parent.FullName == "System.Object")
                return null;
            if (!Running)
                return null;
            #region Internal methods
            //Make sure that RVA is not zero. If its zero, than its extern
            if (m.IsInternalCall | m.IsImplementedByRuntime)
            {
                string properName = m.Name;
                if (m.IsImplementedByRuntime)
                {
                    properName = m.Parent.FullName.Replace(".", "_") + "." + m.Name + "_impl";
                }
                foreach (var item in CustomInternalMethods)
                {
                    if (item.Key == properName)
                    {
                        MethodArgStack a = null;
                        item.Value.Invoke(parms.ToArray(), ref a, m);

                        //Don't forget to remove item parms
                        if (m.AmountOfParms == 0)
                        {
                            //no need to do anything
                        }
                        else
                        {
                            int StartParmIndex = -1;
                            int EndParmIndex = -1;
                            int paramsRead = 0;
                            bool foundEndIndex = false;
                            if (m.AmountOfParms > 0)
                            {
                                var endParam = m.SignatureInfo.Params[m.SignatureInfo.Params.Count - 1];
                                for (int i4 = stack.Count - 1; i4 >= 0; i4--)
                                {
                                    var stackitm = stack[i4];
                                    if (stackitm.type == endParam.type | stackitm.type == StackItemType.ldnull && StartParmIndex == -1 && !foundEndIndex)
                                    {
                                        if (endParam.IsClass)
                                        {
                                            if (endParam.ClassType != stackitm.ObjectType)
                                            {
                                                continue;
                                            }
                                        }
                                        EndParmIndex = i4;
                                        foundEndIndex = true;
                                        if (m.AmountOfParms == 1)
                                        {
                                            StartParmIndex = i4;
                                            break;
                                        }
                                    }
                                    if (EndParmIndex != -1 && StartParmIndex == -1)
                                    {
                                        paramsRead++;
                                    }
                                    if (EndParmIndex != -1 && paramsRead >= m.AmountOfParms)
                                    {
                                        StartParmIndex = i4;
                                        break;
                                    }
                                }
                            }
                            if (StartParmIndex == -1)
                            {
                                if (m.AmountOfParms == 1)
                                    return a;
                                else
                                {
                                    //clrError("Failed to find parameters after exectuting an internal method!", "internal CLR error");
                                    return a;
                                }
                            }

                            if (m.AmountOfParms == 1)
                            {
                                stack.RemoveAt(StartParmIndex);
                            }
                            else
                            {
                                stack.RemoveRange(StartParmIndex, EndParmIndex - StartParmIndex + 1);
                            }
                        }


                        if (m.Name.Contains("ToString"))
                        {
                            stack.RemoveAt(stack.Count - 1);
                        }
                        return a;
                    }
                }

                clrError("Cannot find internal method: " + properName, "internal CLR error");
                return null;
            }
            else if (m.RVA == 0)
            {
                clrError($"Cannot find the method body for {m.Parent.FullName}.{m.Name}", "System.Exception");
                return null;
            }
            #endregion
            MethodArgStack[] Localstack = new MethodArgStack[256];
            if (addToCallStack)
            {
                //Add this method to the callstack.
                CallStack.Add(new CallStackItem() { method = m });
            }

            //Now decompile the code and run it
            var decompiler = new IlDecompiler(m);
            foreach (var item in dlls)
            {
                decompiler.AddReference(item.Value);
            }
            var code = decompiler.Decompile();
            int i;
            for (i = 0; i < code.Length; i++)
            {
                if (!Running)
                    return null;
                var item = code[i];
                #region Ldloc / stloc
                if (item.OpCodeName == "stloc.s")
                {
                    if (ThrowIfStackIsZero(stack, "stloc.s")) return null;

                    var oldItem = stack[stack.Count - 1];
                    Localstack[(byte)item.Operand] = oldItem;
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (item.OpCodeName == "stloc.0")
                {
                    if (ThrowIfStackIsZero(stack, "stloc.0")) return null;

                    var oldItem = stack[stack.Count - 1];
                    Localstack[0] = oldItem;
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (item.OpCodeName == "stloc.1")
                {
                    if (ThrowIfStackIsZero(stack, "stloc.1")) return null;
                    var oldItem = stack[stack.Count - 1];

                    Localstack[1] = oldItem;
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (item.OpCodeName == "stloc.2")
                {
                    if (ThrowIfStackIsZero(stack, "stloc.2")) return null;
                    var oldItem = stack[stack.Count - 1];

                    Localstack[2] = oldItem;
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (item.OpCodeName == "stloc.3")
                {
                    if (ThrowIfStackIsZero(stack, "stloc.3")) return null;
                    var oldItem = stack[stack.Count - 1];

                    Localstack[3] = oldItem;
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (item.OpCodeName == "ldloc.s")
                {
                    var oldItem = Localstack[(byte)item.Operand];
                    stack.Add(oldItem);
                }
                else if (item.OpCodeName == "ldloca.s")
                {
                    var oldItem = Localstack[(byte)item.Operand];

                    if (oldItem == null)
                    {
                        Localstack[(byte)item.Operand] = MethodArgStack.Null();
                        stack.Add(Localstack[(byte)item.Operand]);
                    }
                    else
                    {
                        stack.Add(oldItem);
                    }
                }
                else if (item.OpCodeName == "ldloc.0")
                {
                    var oldItem = Localstack[0];
                    stack.Add(oldItem);
                    // Localstack[0] = null;
                }
                else if (item.OpCodeName == "ldloc.1")
                {
                    var oldItem = Localstack[1];
                    stack.Add(oldItem);

                    //Localstack[1] = null;
                }
                else if (item.OpCodeName == "ldloc.2")
                {
                    var oldItem = Localstack[2];
                    stack.Add(oldItem);
                    //Localstack[2] = null;
                }
                else if (item.OpCodeName == "ldloc.3")
                {
                    var oldItem = Localstack[3];
                    stack.Add(oldItem);
                    //Localstack[3] = null;
                }
                #endregion
                #region ldc* opcodes
                //Push int32
                else if (item.OpCodeName == "ldc.i4")
                {
                    //Puts an int32 onto the arg stack
                    stack.Add(MethodArgStack.Int32((int)item.Operand));
                }
                else if (item.OpCodeName == "ldc.i4.0")
                {
                    //Puts an 0 onto the arg stack
                    stack.Add(MethodArgStack.Int32(0));
                }
                else if (item.OpCodeName == "ldc.i4.1")
                {
                    //Puts an int32 with value 1 onto the arg stack
                    stack.Add(MethodArgStack.Int32(1));
                }
                else if (item.OpCodeName == "ldc.i4.2")
                {
                    //Puts an int32 with value 2 onto the arg stack
                    stack.Add(MethodArgStack.Int32(2));
                }
                else if (item.OpCodeName == "ldc.i4.3")
                {
                    //Puts an int32 with value 3 onto the arg stack
                    stack.Add(MethodArgStack.Int32(3));
                }
                else if (item.OpCodeName == "ldc.i4.4")
                {
                    //Puts an int32 with value 4 onto the arg stack
                    stack.Add(MethodArgStack.Int32(4));
                }
                else if (item.OpCodeName == "ldc.i4.5")
                {
                    //Puts an int32 with value 5 onto the arg stack
                    stack.Add(MethodArgStack.Int32(5));
                }
                else if (item.OpCodeName == "ldc.i4.6")
                {
                    //Puts an int32 with value 6 onto the arg stack
                    stack.Add(MethodArgStack.Int32(6));
                }
                else if (item.OpCodeName == "ldc.i4.7")
                {
                    //Puts an int32 with value 7 onto the arg stack
                    stack.Add(MethodArgStack.Int32(7));
                }
                else if (item.OpCodeName == "ldc.i4.8")
                {
                    //Puts an int32 with value 8 onto the arg stack
                    stack.Add(MethodArgStack.Int32(8));
                }
                else if (item.OpCodeName == "ldc.i4.m1")
                {
                    //Puts an int32 with value -1 onto the arg stack
                    stack.Add(MethodArgStack.Int32(-1));
                }
                else if (item.OpCodeName == "ldc.i4.s")
                {
                    //Push an int32 onto the stack
                    stack.Add(MethodArgStack.Int32((int)(sbyte)(byte)item.Operand));
                }
                //Push int64
                else if (item.OpCodeName == "ldc.i8")
                {
                    stack.Add(MethodArgStack.Int64((long)item.Operand));
                }
                //push float32
                else if (item.OpCodeName == "ldc.r4")
                {
                    //Puts an float32 with value onto the arg stack
                    stack.Add(MethodArgStack.Float32((float)item.Operand));
                }
                //Push float64
                else if (item.OpCodeName == "ldc.r8")
                {
                    //Puts an float64 with value onto the arg stack
                    stack.Add(MethodArgStack.Float64((double)item.Operand));
                }
                #endregion
                #region conv* opcodes
                else if (item.OpCodeName == "conv.i4")
                {
                    if (ThrowIfStackIsZero(stack, "conv.i4")) return null;

                    var numb = stack[stack.Count - 1];
                    if (numb.type == StackItemType.Int32)
                    {
                        //We don't need to do anything because it's already int32
                    }
                    else if (numb.type == StackItemType.Float32)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(MethodArgStack.Int32((int)(float)numb.value));
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }
                else if (item.OpCodeName == "conv.r4")
                {
                    if (ThrowIfStackIsZero(stack, "conv.r4")) return null;

                    var numb = stack[stack.Count - 1];
                    if (numb.type == StackItemType.Int32)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(MethodArgStack.Float32((int)numb.value));
                    }
                    else if (numb.type == StackItemType.Float32)
                    {
                        //We don't need to do anything because it's already float32
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }
                #endregion
                #region Ldind* opcodes
                else if (item.OpCodeName == "ldind.u1")
                {
                    //TODO: implement this opcode
                }
                else if (item.OpCodeName == "ldind.u2")
                {
                    //TODO: implement this opcode
                }
                else if (item.OpCodeName == "ldind.u4")
                {
                    //TODO: implement this opcode
                }
                else if (item.OpCodeName == "ldind.i1")
                {
                    //TODO: implement this opcode
                }
                else if (item.OpCodeName == "ldind.i2")
                {
                    //TODO: implement this opcode
                }
                else if (item.OpCodeName == "ldind.i4")
                {
                    //TODO: implement this opcode
                }
                else if (item.OpCodeName == "ldind.i8")
                {
                    //TODO: implement this opcode
                }
                #endregion
                #region Math
                else if (item.OpCodeName == "add")
                {
                    var a = stack.Pop();
                    var b = stack.Pop();

                    stack.Add(MathOperations.Op(b, a, MathOperations.Operation.Add));
                }
                else if (item.OpCodeName == "sub")
                {
                    var a = stack.Pop();
                    var b = stack.Pop();

                    stack.Add(MathOperations.Op(b, a, MathOperations.Operation.Subtract));
                }
                else if (item.OpCodeName == "div")
                {
                    var a = stack.Pop();
                    var b = stack.Pop();

                    stack.Add(MathOperations.Op(b, a, MathOperations.Operation.Divide));
                }
                else if (item.OpCodeName == "mul")
                {
                    var a = stack.Pop();
                    var b = stack.Pop();

                    stack.Add(MathOperations.Op(b, a, MathOperations.Operation.Multiply));
                }
                else if (item.OpCodeName == "rem")
                {
                    var a = stack.Pop();
                    var b = stack.Pop();

                    stack.Add(MathOperations.Op(b, a, MathOperations.Operation.Remainder));
                }
                else if (item.OpCodeName == "neg")
                {
                    var a = stack.Pop();

                    stack.Add(MathOperations.Op(a, MathOperations.Operation.Negate));
                }
                else if (item.OpCodeName == "and")
                {
                    var a = stack.Pop();
                    var b = stack.Pop();

                    if (a.type != StackItemType.Int32 || b.type != StackItemType.Int32)
                    {
                        clrError($"Error in {item.OpCodeName} opcode: type is not int32", "Internal CLR error");
                        return null;
                    }

                    var numb1 = (int)a.value;
                    var numb2 = (int)b.value;
                    var result = numb1 & numb2;
                    stack.Add(MethodArgStack.Int32(result));
                }
                else if (item.OpCodeName == "or")
                {
                    var a = stack.Pop();
                    var b = stack.Pop();

                    if (a.type != StackItemType.Int32 || b.type != StackItemType.Int32)
                    {
                        clrError($"Error in {item.OpCodeName} opcode: type is not int32", "Internal CLR error");
                        return null;
                    }

                    var numb1 = (int)a.value;
                    var numb2 = (int)b.value;
                    var result = numb1 | numb2;
                    stack.Add(MethodArgStack.Int32(result));
                }
                else if (item.OpCodeName == "xor")
                {
                    var a = stack.Pop();
                    var b = stack.Pop();

                    if (a.type != StackItemType.Int32 || b.type != StackItemType.Int32)
                    {
                        clrError($"Error in {item.OpCodeName} opcode: type is not int32", "Internal CLR error");
                        return null;
                    }

                    var numb1 = (int)a.value;
                    var numb2 = (int)b.value;
                    var result = numb1 ^ numb2;
                    stack.Add(MethodArgStack.Int32(result));
                }
                else if (item.OpCodeName == "shl")
                {
                    var a = stack.Pop();
                    var b = stack.Pop();

                    if (a.type != StackItemType.Int32 || b.type != StackItemType.Int32)
                    {
                        clrError($"Error in {item.OpCodeName} opcode: type is not int32", "Internal CLR error");
                        return null;
                    }

                    var numb1 = (int)a.value;
                    var numb2 = (int)b.value;
                    var result = numb2 << numb1;
                    stack.Add(MethodArgStack.Int32(result));
                }
                else if (item.OpCodeName == "shr")
                {
                    var a = stack.Pop();
                    var b = stack.Pop();

                    if (a.type != StackItemType.Int32 || b.type != StackItemType.Int32)
                    {
                        clrError($"Error in {item.OpCodeName} opcode: type is not int32", "Internal CLR error");
                        return null;
                    }

                    var numb1 = (int)a.value;
                    var numb2 = (int)b.value;
                    var result = numb2 >> numb1;
                    stack.Add(MethodArgStack.Int32(result));
                }
                else if (item.OpCodeName == "not")
                {
                    var a = stack.Pop();

                    if (a.type != StackItemType.Int32)
                    {
                        clrError($"Error in {item.OpCodeName} opcode: type is not int32", "Internal CLR error");
                        return null;
                    }

                    var numb1 = (int)a.value;
                    var result = ~numb1;
                    stack.Add(MethodArgStack.Int32(result));
                }
                else if (item.OpCodeName == "ceq")
                {
                    if (stack.Count < 2)
                    {
                        clrError($"There has to be 2 or more items on the stack for {item.OpCodeName} instruction to work!", "Internal CLR error");
                        return null;
                    }

                    var a = stack.Pop();
                    var b = stack.Pop();

                    stack.Add(MathOperations.Op(b, a, MathOperations.Operation.Equal));
                }
                else if (item.OpCodeName == "cgt")
                {
                    if (stack.Count < 2)
                    {
                        clrError($"There has to be 2 or more items on the stack for {item.OpCodeName} instruction to work!", "Internal CLR error");
                        return null;
                    }

                    var a = stack.Pop();
                    var b = stack.Pop();

                    stack.Add(MathOperations.Op(b, a, MathOperations.Operation.GreaterThan));
                }
                else if (item.OpCodeName == "cgt.un")
                {
                    if (stack.Count < 2)
                    {
                        clrError($"There has to be 2 or more items on the stack for {item.OpCodeName} instruction to work!", "Internal CLR error");
                        return null;
                    }

                    var a = stack.Pop();
                    var b = stack.Pop();

                    stack.Add(MathOperations.Op(b, a, MathOperations.Operation.GreaterThan));
                }
                else if (item.OpCodeName == "clt")
                {
                    if (stack.Count < 2)
                    {
                        clrError($"There has to be 2 or more items on the stack for {item.OpCodeName} instruction to work!", "Internal CLR error");
                        return null;
                    }

                    var a = stack.Pop();
                    var b = stack.Pop();

                    stack.Add(MathOperations.Op(b, a, MathOperations.Operation.LessThan));
                }
                else if (item.OpCodeName == "clt.un")
                {
                    if (stack.Count < 2)
                    {
                        clrError($"There has to be 2 or more items on the stack for {item.OpCodeName} instruction to work!", "Internal CLR error");
                        return null;
                    }

                    var a = stack.Pop();
                    var b = stack.Pop();

                    stack.Add(MathOperations.Op(b, a, MathOperations.Operation.LessThan));
                }
                else if (item.OpCodeName == "beq" || item.OpCodeName == "beq.s")
                {
                    BranchWithOp(item, stack, decompiler, MathOperations.Operation.Equal, ref i);
                }
                else if (item.OpCodeName == "bge" || item.OpCodeName == "bge.s")
                {
                    BranchWithOp(item, stack, decompiler, MathOperations.Operation.GreaterThanEqual, ref i);
                }
                // TODO: Implement bge.un and bge.un.s
                else if (item.OpCodeName == "bgt" || item.OpCodeName == "bgt.s")
                {
                    BranchWithOp(item, stack, decompiler, MathOperations.Operation.GreaterThan, ref i);
                }
                // TODO: Implement bgt.un and bgt.un.s
                else if (item.OpCodeName == "ble" || item.OpCodeName == "ble.s")
                {
                    BranchWithOp(item, stack, decompiler, MathOperations.Operation.LessThanEqual, ref i);
                }
                // TODO: Implement ble.un and ble.un.s
                else if (item.OpCodeName == "blt" || item.OpCodeName == "blt.s")
                {
                    BranchWithOp(item, stack, decompiler, MathOperations.Operation.LessThan, ref i);
                }
                // TODO: Implement blt.un and blt.un.s
                else if (item.OpCodeName == "bne.un" || item.OpCodeName == "bne.un.s")
                {
                    BranchWithOp(item, stack, decompiler, MathOperations.Operation.NotEqual, ref i);
                }
                #endregion
                #region Branch instructions
                else if (item.OpCodeName == "br" || item.OpCodeName == "br.s")
                {
                    //find the ILInstruction that is in this position
                    int i2 = item.Position + (int)item.Operand + 1;
                    ILInstruction inst = decompiler.GetInstructionAtOffset(i2, -1);

                    if (inst == null)
                        throw new Exception("Attempt to branch to null");
                    i = inst.RelPosition - 1;
                }
                else if (item.OpCodeName == "brfalse.s" || item.OpCodeName == "brfalse")
                {
                    if (stack.Count == 0)
                    {
                        clrError("Do not know if I should branch, because there is nothing on the stack. Instruction: brfalse.s", "Internal");
                        return null;
                    }
                    var s = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    bool exec = false;
                    if (s.value == null)
                        exec = true;
                    else
                    {
                        try
                        {
                            if ((int)s.value == 0)
                                exec = true;
                        }
                        catch { }
                    }
                    if (exec)
                    {
                        // find the ILInstruction that is in this position
                        int i2 = item.Position + (int)item.Operand + 1;
                        ILInstruction inst = decompiler.GetInstructionAtOffset(i2, -1);

                        if (inst == null)
                            throw new Exception("Attempt to branch to null");
                        i = inst.RelPosition - 1;
                    }
                }
                else if (item.OpCodeName == "brtrue.s" || item.OpCodeName == "brtrue")
                {
                    if (stack[stack.Count - 1].value == null)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }
                    bool exec = false;
                    if (stack[stack.Count - 1].type != StackItemType.Int32)
                    {
                        if (stack[stack.Count - 1].type != StackItemType.ldnull)
                        {
                            exec = true;
                        }
                    }
                    else
                    {
                        if ((int)stack[stack.Count - 1].value == 1)
                        {
                            exec = true;
                        }
                    }
                    if (exec)
                    {
                        // find the ILInstruction that is in this position
                        int i2 = item.Position + (int)item.Operand + 1;
                        ILInstruction inst = decompiler.GetInstructionAtOffset(i2, -1);

                        if (inst == null)
                            throw new Exception("Attempt to branch to null");
                        stack.RemoveAt(stack.Count - 1);
                        i = inst.RelPosition - 1;
                    }
                    else
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
                #endregion
                #region Misc
                else if (item.OpCodeName == "ldstr")
                {
                    stack.Add(MethodArgStack.String((string)item.Operand));
                }
                else if (item.OpCodeName == "nop")
                {
                    //Don't do anything
                }
                else if (item.OpCodeName == "conv.i8")
                {
                    var itm = stack[stack.Count - 1];
                    if (itm.type == StackItemType.Int32)
                    {
                        itm.value = (long)(int)itm.value;
                        itm.type = StackItemType.Int64;
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }

                    stack.RemoveAt(stack.Count - 1);
                    stack.Add(itm);
                }
                else if (item.OpCodeName == "ldsfld")
                {
                    //get value from static field
                    DotNetField f2 = null;
                    FieldInfo info = item.Operand as FieldInfo;
                    foreach (var f in m.Parent.File.Types)
                    {
                        foreach (var tttt in f.Fields)
                        {
                            if (tttt.IndexInTabel == info.IndexInTabel && tttt.Name == info.Name)
                            {
                                f2 = tttt;
                                break;
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(info.Class) && f2 == null)
                    {
                        throw new Exception("Could not find the field");
                    }
                    if (f2 == null)
                    {
                        foreach (var dll in dlls)
                        {
                            foreach (var type in dll.Value.Types)
                            {
                                foreach (var f in type.Fields)
                                {
                                    if (f.Name == info.Name && type.Name == info.Class && type.NameSpace == info.Namespace)
                                    {
                                        f2 = f;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (f2 == null)
                        throw new Exception("Cannot find the static field to read from.");

                    StaticField f3 = null;
                    foreach (var f in StaticFieldHolder.staticFields)
                    {
                        if (f.theField.Name == f2.Name && f.theField.ParrentType.FullName == f2.ParrentType.FullName)
                        {
                            f3 = f;
                            break;
                        }
                    }
                    if (f3 == null)
                    {
                        stack.Add(MethodArgStack.ldnull);
                    }
                    else
                    {
                        stack.Add(f3.value);
                    }
                }
                else if (item.OpCodeName == "stsfld")
                {
                    FieldInfo info = item.Operand as FieldInfo;
                    //write value to field.
                    DotNetField f2 = null;
                    foreach (var f in m.Parent.File.Types)
                    {
                        foreach (var tttt in f.Fields)
                        {
                            if (tttt.IndexInTabel == info.IndexInTabel && tttt.Name == info.Name)
                            {
                                f2 = tttt;
                                break;
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(info.Class) && f2 == null)
                    {
                        throw new Exception("Could not find the field");
                    }
                    if (f2 == null)
                    {
                        foreach (var dll in dlls)
                        {
                            foreach (var type in dll.Value.Types)
                            {
                                foreach (var f in type.Fields)
                                {
                                    if (f.Name == info.Name && type.Name == info.Class && type.NameSpace == info.Namespace)
                                    {
                                        f2 = f;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (f2 == null)
                    {
                        throw new InvalidOperationException("Cannot find the field to write to.");
                    }
                    StaticField f3 = null;
                    foreach (var f in StaticFieldHolder.staticFields)
                    {
                        if (f.theField.Name == f2.Name && f.theField.ParrentType.FullName == f2.ParrentType.FullName)
                        {
                            f3 = f;

                            f.value = stack[stack.Count - 1];
                            break;
                        }
                    }

                    if (f3 == null)
                    {
                        //create field
                        StaticFieldHolder.staticFields.Add(new StaticField() { theField = f2, value = stack[stack.Count - 1] });
                    }
                    if (f2 == null)
                        throw new Exception("Cannot find the field.");
                    f2.Value = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (item.OpCodeName == "call")
                {
                    var call = (InlineMethodOperandData)item.Operand;
                    if (!InternalCallMethod(call, m, addToCallStack, false, false, stack))
                    {
                        return null;
                    }
                }
                else if (item.OpCodeName == "ldnull")
                {
                    stack.Add(MethodArgStack.Null());
                }
                else if (item.OpCodeName == "throw")
                {
                    //Throw Exception
                    var exp = stack[stack.Count - 1];

                    if (exp.type == StackItemType.ldnull)
                    {
                        clrError("Object reference not set to an instance of an object.", "System.NullReferenceException");
                        return null;
                    }
                    else if (exp.type == StackItemType.Object)
                    {
                        var obj = Objects.ObjectRefs[(int)exp.value];
                        clrError((string)obj.Fields["_message"].value, "System.Exception");
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }
                else if (item.OpCodeName == "ret")
                {
                    //Return from function
                    MethodArgStack a = null;
                    if (stack.Count != 0 && m.HasReturnValue)
                    {
                        a = stack[stack.Count - 1];
                        if (addToCallStack)
                            CallStack.RemoveAt(CallStack.Count - 1);


                        stack.RemoveAt(stack.Count - 1);
                    }

                    return a;
                }
                else if (item.OpCodeName == "newobj")
                {
                    var call = (InlineMethodOperandData)item.Operand;
                    //Local/Defined method
                    DotNetMethod m2 = null;
                    foreach (var item2 in dlls)
                    {
                        foreach (var item3 in item2.Value.Types)
                        {
                            foreach (var meth in item3.Methods)
                            {
                                if (meth.RVA == call.RVA && meth.Name == call.FunctionName && meth.Signature == call.Signature && item3.Name == call.ClassName)
                                {
                                    if (call.ParamListIndex != 0)
                                    {
                                        if (meth.ParamListIndex == call.ParamListIndex)
                                        {
                                            m2 = meth;
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        m2 = meth;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (m2 == null)
                    {
                        clrError($"Cannot find the called constructor: {call.NameSpace}.{call.ClassName}.{call.FunctionName}(). Function signature is {call.Signature}", "CLR internal error");
                        return null;
                    }
                    var a = CreateObject(m2, stack);

                    stack.Add(a);
                }
                else if (item.OpCodeName == "stfld")
                {
                    //write value to field.
                    FieldInfo info = item.Operand as FieldInfo;
                    DotNetField f2 = null;
                    foreach (var f in m.Parent.File.Types)
                    {
                        foreach (var tttt in f.Fields)
                        {
                            if (tttt.IndexInTabel == info.IndexInTabel && tttt.Name == info.Name)
                            {
                                f2 = tttt;
                                break;
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(info.Class) && f2 == null)
                    {
                        throw new Exception("Could not find the field");
                    }
                    if (f2 == null)
                    {
                        foreach (var dll in dlls)
                        {
                            foreach (var type in dll.Value.Types)
                            {
                                foreach (var f in type.Fields)
                                {
                                    if (f.Name == info.Name && type.Name == info.Class && type.NameSpace == info.Namespace)
                                    {
                                        f2 = f;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (f2 == null)
                    {
                        clrError("Failed to resolve field for writing.", "");
                        return null;
                    }
                    MethodArgStack obj = stack[stack.Count - 2];
                    if (obj == null)
                    {
                        clrError("Failed to find correct type in the stack", "");
                        return null;
                    }

                    var data = Objects.ObjectRefs[(int)obj.value];
                    if (data.Fields.ContainsKey(f2.Name))
                    {
                        Objects.ObjectRefs[(int)obj.value].Fields[f2.Name] = stack[stack.Count - 1];
                    }
                    else
                    {
                        Objects.ObjectRefs[(int)obj.value].Fields.Add(f2.Name, stack[stack.Count - 1]);
                    }
                    stack.RemoveAt(stack.Count - 1);
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (item.OpCodeName == "ldfld")
                {
                    //read value from field.
                    FieldInfo info = item.Operand as FieldInfo;
                    DotNetField f2 = null;
                    foreach (var f in m.Parent.File.Types)
                    {
                        foreach (var tttt in f.Fields)
                        {
                            if (tttt.IndexInTabel == info.IndexInTabel && tttt.Name == info.Name)
                            {
                                f2 = tttt;
                                break;
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(info.Class) && f2 == null)
                    {
                        throw new Exception("Could not find the field");
                    }
                    if (f2 == null)
                    {
                        foreach (var dll in dlls)
                        {
                            foreach (var type in dll.Value.Types)
                            {
                                foreach (var f in type.Fields)
                                {
                                    if (f.Name == info.Name && type.Name == info.Class && type.NameSpace == info.Namespace)
                                    {
                                        f2 = f;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (f2 == null)
                    {
                        clrError("Failed to resolve field for writing.", "");
                        return null;
                    }
                    MethodArgStack obj = stack[stack.Count - 1];

                    if (obj == null)
                    {
                        clrError("Object to read from not found!", "CLR internal error");
                        return null;
                    }

                    stack.RemoveAt(stack.Count - 1);
                    var data = Objects.ObjectRefs[(int)obj.value];
                    if (data.Fields.ContainsKey(f2.Name))
                    {
                        stack.Add(data.Fields[f2.Name]);
                    }
                    else
                    {
                        clrError("Reading from non existant field on object " + obj.ObjectType.FullName + ", field name is " + f2.Name, "CLR internal error");
                    }
                }
                else if (item.OpCodeName == "ldarg.0")
                {
                    if (parms.Count == 0)
                        continue;
                    stack.Add(parms[0]);
                }
                else if (item.OpCodeName == "ldarg.1")
                {
                    if (parms.Count == 0)
                        continue;

                    stack.Add(parms[1]);
                }
                else if (item.OpCodeName == "ldarg.2")
                {
                    if (parms.Count == 0)
                        continue;

                    stack.Add(parms[2]);
                }
                else if (item.OpCodeName == "ldarg.3")
                {
                    if (parms.Count == 0)
                        continue;

                    stack.Add(parms[3]);
                }
                else if (item.OpCodeName == "ldarg.s")
                {
                    if (parms.Count == 0)
                        continue;

                    stack.Add(parms[(byte)item.Operand]);
                }
                else if (item.OpCodeName == "ldarga.s")
                {
                    if (parms.Count == 0)
                        continue;

                    stack.Add(parms[(byte)item.Operand]);
                }
                else if (item.OpCodeName == "callvirt")
                {
                    var call = (InlineMethodOperandData)item.Operand;
                    if (!InternalCallMethod(call, m, addToCallStack, true, false, stack))
                    {
                        return null;
                    }
                }
                #endregion
                #region Arrays
                else if (item.OpCodeName == "newarr")
                {
                    var arrayLen = stack[stack.Count - 1];
                    var array = Arrays.AllocArray((int)arrayLen.value);

                    stack.RemoveAt(stack.Count - 1);
                    stack.Add(new MethodArgStack() { type = StackItemType.Array, value = array.Index });
                }
                else if (item.OpCodeName == "ldlen")
                {
                    MethodArgStack array = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    stack.Add(MethodArgStack.Int32(Arrays.ArrayRefs[Arrays.GetIndexFromRef(array)].Length));
                }
                else if (item.OpCodeName == "dup")
                {
                    stack.Add(stack[stack.Count - 1]);
                }
                else if (item.OpCodeName == "pop")
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (item.OpCodeName == "ldelem.ref")
                {
                    var index = stack[stack.Count - 1];
                    var array = stack[stack.Count - 2];
                    if (array.type != StackItemType.Array)
                    {
                        clrError("Expected array, but got something else. Fault instruction name: ldelem.ref", "Internal CLR error"); return null;
                    }
                    if (index.type != StackItemType.Int32)
                    {
                        clrError("Expected Int32, but got something else. Fault instruction name: ldelem.ref", "Internal CLR error");
                        return null;
                    }
                    var idx = (int)index.value;
                    stack.RemoveAt(stack.Count - 1); //Remove index
                    stack.RemoveAt(stack.Count - 1); //Remove array

                    stack.Add(Arrays.ArrayRefs[Arrays.GetIndexFromRef(array)].Items[idx]);
                }
                else if (item.OpCodeName == "ldelem")
                {
                    var index = stack[stack.Count - 1];
                    var array = stack[stack.Count - 2];
                    if (array.type != StackItemType.Array)
                    {
                        clrError("Expected array, but got something else. Fault instruction name: ldelem.ref", "Internal CLR error"); return null;
                    }
                    if (index.type != StackItemType.Int32)
                    {
                        clrError("Expected Int32, but got something else. Fault instruction name: ldelem.ref", "Internal CLR error");
                        return null;
                    }
                    var idx = (int)index.value;
                    stack.RemoveAt(stack.Count - 1); //Remove index
                    stack.RemoveAt(stack.Count - 1); //Remove array

                    var i2 = Arrays.GetIndexFromRef(array);
                    stack.Add(Arrays.ArrayRefs[i2].Items[idx]);
                }
                else if (item.OpCodeName == "stelem.ref")
                {
                    var val = stack[stack.Count - 1];
                    var index = stack[stack.Count - 2];
                    var array = stack[stack.Count - 3];
                    //if (array.type != StackItemType.Array) { clrError("Expected array, but got something else. Fault instruction name: stelem.ref", "Internal CLR error"); return null; }
                    if (index.type != StackItemType.Int32) { clrError("Expected Int32, but got something else. Fault instruction name: stelem.ref", "Internal CLR error"); return null; }

                    Arrays.ArrayRefs[Arrays.GetIndexFromRef(array)].Items[(int)index.value] = val;

                    stack.RemoveAt(stack.Count - 1); //Remove value
                    stack.RemoveAt(stack.Count - 1); //Remove index
                    stack.RemoveAt(stack.Count - 1); //Remove array ref
                }
                else if (item.OpCodeName == "stelem")
                {
                    var val = stack[stack.Count - 1];
                    var index = stack[stack.Count - 2];
                    var array = stack[stack.Count - 3];
                    //if (array.type != StackItemType.Array) { clrError("Expected array, but got something else. Fault instruction name: stelem", "Internal CLR error"); return null; }
                    var idx = Arrays.GetIndexFromRef(array);
                    Arrays.ArrayRefs[idx].Items[(int)index.value] = val;

                    stack.RemoveAt(stack.Count - 1); //Remove value
                    stack.RemoveAt(stack.Count - 1); //Remove index
                    stack.RemoveAt(stack.Count - 1); //Remove array ref
                }
                else if (item.OpCodeName == "stelem.i2")
                {
                    var val = stack[stack.Count - 1];
                    var index = stack[stack.Count - 2];
                    var array = stack[stack.Count - 3];
                    //if (array.type != StackItemType.Array) { clrError("Expected array, but got something else. Fault instruction name: stelem", "Internal CLR error"); return null; }
                    var idx = Arrays.GetIndexFromRef(array);
                    Arrays.ArrayRefs[idx].Items[(int)index.value] = val;

                    stack.RemoveAt(stack.Count - 1); //Remove value
                    stack.RemoveAt(stack.Count - 1); //Remove index
                    stack.RemoveAt(stack.Count - 1); //Remove array ref
                }
                #endregion
                #region Reflection
                else if (item.OpCodeName == "ldtoken")
                {
                    var index = (FieldInfo)item.Operand;
                    DotNetType t2 = null;
                    if (index.IsInFieldTabel)
                    {
                        foreach (var t in m.Parent.File.Types)
                        {
                            if (t.Name == index.Name && t.NameSpace == index.Namespace)
                            {
                                t2 = t;
                            }
                        }
                    }
                    else
                    {
                        foreach (var dll in dlls)
                        {
                            foreach (var t in dll.Value.Types)
                            {
                                if (t.Name == index.Name && t.NameSpace == index.Namespace)
                                {
                                    t2 = t;
                                }
                            }
                        }
                    }
                    if (t2 == null)
                    {
                        clrError("Failed to resolve token. OpCode: ldtoken. Type: " + index.Namespace + "." + index.Name, "Internal CLR error");
                        return null;
                    }

                    var handle = CreateType("System", "RuntimeTypeHandle");
                    WriteStringToType(handle, "_name", t2.Name);
                    WriteStringToType(handle, "_namespace", t2.NameSpace);
                    stack.Add(handle);
                }
                else if (item.OpCodeName == "ldftn")
                {
                    var call = item.Operand as InlineMethodOperandData; //a method
                    var obj2 = stack[stack.Count - 1]; //the compiler generated object to call the method on
                    DotNetMethod m2 = null;

                    if (call.RVA == 0) throw new NotImplementedException();
                    else
                    {
                        foreach (var item2 in dlls)
                        {
                            foreach (var item3 in item2.Value.Types)
                            {
                                foreach (var meth in item3.Methods)
                                {
                                    var fullName = call.NameSpace + "." + call.ClassName;
                                    if (string.IsNullOrEmpty(call.NameSpace))
                                        fullName = call.ClassName;

                                    if (meth.RVA == call.RVA && meth.Name == call.FunctionName && meth.Signature == call.Signature && meth.Parent.FullName == fullName)
                                    {
                                        m2 = meth;
                                        break;
                                    }
                                }
                            }
                        }

                        if (m2 == null)
                        {
                            clrError($"Cannot resolve virtual called method: {call.NameSpace}.{call.ClassName}.{call.FunctionName}(). Function signature is {call.Signature}", "");
                            return null;
                        }
                    }

                    var ptr = CreateType("System", "IntPtr");
                    Objects.ObjectRefs[(int)ptr.value].Fields.Add("PtrToMethod", new MethodArgStack() { value = m2, type = StackItemType.MethodPtr });
                    stack.Add(ptr);
                }
                else if (item.OpCodeName == "leave.s")
                {
                    // find the ILInstruction that is in this position
                    int i2 = item.Position + (int)item.Operand + 1;
                    ILInstruction inst = decompiler.GetInstructionAtOffset(i2, -1);

                    if (inst == null)
                        throw new Exception("Attempt to branch to null");
                    stack.RemoveAt(stack.Count - 1);
                    i = inst.RelPosition - 1;
                }
                else if (item.OpCodeName == "stind.i4")
                {
                    var val = stack[stack.Count - 1];
                    var ptr = stack[stack.Count - 2];
                    if (ptr.type != StackItemType.Int32) throw new InvalidOperationException("Invaild pointer!");

                    ptr.value = val.value;
                    stack[stack.Count - 2] = val; //just in case
                    stack.RemoveAt(stack.Count - 1); //remove value
                }
                else if (item.OpCodeName == "starg.s")
                {
                    var val = stack[stack.Count - 1];
                    var a = (byte)item.Operand;
                    if (stack.Count == 1 && a == 1)
                    {
                        stack.Add(val);
                    }
                    else
                    {
                        stack[a] = val;
                    }

                    stack.RemoveAt(stack.Count - 1);
                }
                else if (item.OpCodeName == "ldobj")
                {
                    stack.Add(stack[0]);
                }
                else if (item.OpCodeName == "initobj")
                {
                    stack.Pop();
                    stack.Add(MethodArgStack.Null());
                }
                else if (item.OpCodeName == "ldflda")
                {
                    var obj = stack.Pop();
                    var type = obj.ObjectType;
                    var fld = (FieldInfo)item.Operand;
                    if (fld.Name == null)
                    {
                        throw new NotImplementedException();
                    }
                    DotNetField theField = null;
                    int idx = 0;
                    foreach (var f in type.Fields)
                    {
                        if (f.Name == fld.Name)
                        {
                            theField = f;
                        }
                        idx++;
                    }
                    if (theField == null)
                        throw new NotImplementedException();


                    stack.Add(MethodArgStack.Int32(idx));
                }
                else if (item.OpCodeName == "box")
                {
                    ;
                }
                #endregion
                else
                {
                    clrError("Unsupported opcode: " + item.OpCodeName, "CLR Internal error");
                    return null;
                }
            }
            return null;
        }
        /// <summary>
        /// Creates an instance of an object
        /// </summary>
        /// <param name="Constructor">The class's constructor method</param>
        /// <param name="Constructorparamss">The parameters of the constructor. Can be an empty list if there are no parameters</param>
        /// <returns>A ready object to be pushed to the stack</returns>
        /// <exception cref="NotImplementedException"></exception>
        public MethodArgStack CreateObject(DotNetMethod Constructor, CustomList<MethodArgStack> Constructorparamss)
        {
            var objAddr = Objects.AllocObject().Index;
            MethodArgStack a = new MethodArgStack() { ObjectContructor = Constructor, ObjectType = Constructor.Parent, type = StackItemType.Object, value = objAddr };

            //init fields
            foreach (var f in Constructor.Parent.Fields)
            {
                switch (f.ValueType.type)
                {
                    case StackItemType.String:
                        Objects.ObjectRefs[objAddr].Fields.Add(f.Name, MethodArgStack.Null());
                        break;
                    case StackItemType.Int32:
                        Objects.ObjectRefs[objAddr].Fields.Add(f.Name, MethodArgStack.Int32(0));
                        break;
                    case StackItemType.Int64:
                        Objects.ObjectRefs[objAddr].Fields.Add(f.Name, MethodArgStack.Int64(0));
                        break;
                    case StackItemType.Float32:
                        Objects.ObjectRefs[objAddr].Fields.Add(f.Name, MethodArgStack.Float32(0));
                        break;
                    case StackItemType.Float64:
                        Objects.ObjectRefs[objAddr].Fields.Add(f.Name, MethodArgStack.Float64(0));
                        break;
                    case StackItemType.Object:
                        Objects.ObjectRefs[objAddr].Fields.Add(f.Name, MethodArgStack.Null());
                        break;
                    case StackItemType.Array:
                        Objects.ObjectRefs[objAddr].Fields.Add(f.Name, MethodArgStack.Null());
                        break;
                    case StackItemType.Any:
                        Objects.ObjectRefs[objAddr].Fields.Add(f.Name, MethodArgStack.Null());
                        break;
                    case StackItemType.Boolean:
                        Objects.ObjectRefs[objAddr].Fields.Add(f.Name, MethodArgStack.Bool(false));
                        break;
                    default:
                        throw new NotImplementedException();
                }
            }

            //Call the contructor
            if (!InternalCallMethod(new InlineMethodOperandData() { ClassName = Constructor.Parent.Name, FunctionName = Constructor.Name, NameSpace = Constructor.Parent.NameSpace, Signature = Constructor.Signature, RVA = Constructor.RVA }, null, true, true, true, Constructorparamss, a))
            {
                Console.WriteLine("Error occured, returning null.");
                return null; //error occured
            }
            return a;
        }

        /// <summary>
        /// Branches to a target instruction (short form) if a given operation returns true.
        /// Pops two elements off the stack as part of the comparison.
        /// </summary>
        /// <param name="stack"></param>
        /// <param name="op"></param>
        private void BranchWithOp(ILInstruction instruction, CustomList<MethodArgStack> stack, IlDecompiler decompiler, MathOperations.Operation op, ref int i)
        {
            var a = stack.Pop();
            var b = stack.Pop();

            bool invert = false;
            if (op == MathOperations.Operation.NotEqual)
            {
                op = MathOperations.Operation.Equal;
                invert = true;
            }

            var result = MathOperations.Op(b, a, op);
            if (invert) result.value = (int)result.value == 1 ? 0 : 1;

            if ((int)result.value == 1)
            {
                int i2 = instruction.Position + (int)instruction.Operand + 1;
                ILInstruction inst = decompiler.GetInstructionAtOffset(i2, -1);

                if (inst == null)
                    throw new Exception("Attempt to branch to null");
                i = inst.RelPosition - 1;
            }
        }
        /// <summary>
        /// Returns true if there is a problem
        /// </summary>
        /// <param name="stack"></param>
        /// <param name="instruction"></param>
        /// <returns></returns>
        private bool ThrowIfStackIsZero(CustomList<MethodArgStack> stack, string instruction)
        {
            if (stack.Count == 0)
            {
                clrError("Fatal error: The " + instruction + " requires more than 1 items on the stack.", "Internal");
                return true;
            }
            return false;
        }
        #region Utils
        private void PrintColor(string s, ConsoleColor c)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = c;
            Console.WriteLine(s);
            Console.ForegroundColor = old;
        }
        private void clrError(string message, string errorType)
        {
            //Running = false;
            PrintColor($"A {errorType} has occured in {file.Backend.ClrStringsStream.GetByOffset(file.Backend.Tabels.ModuleTabel[0].Name)}. The error is: {message}", ConsoleColor.Red);

            CallStack.Reverse();
            string stackTrace = "";
            foreach (var itm in CallStack)
            {
                stackTrace += "at " + itm.method.Parent.NameSpace + "." + itm.method.Parent.Name + "." + itm.method.Name + "()\n";
            }
            if (stackTrace.Length > 0)
            {
                stackTrace = stackTrace.Substring(0, stackTrace.Length - 1); //Remove last \n
                //PrintColor(stackTrace, ConsoleColor.Red);
            }
        }
        public void RunMethodInDLL(string NameSpace, string TypeName, string MethodName)
        {
            foreach (var dll in dlls)
            {
                foreach (var type in dll.Value.Types)
                {
                    foreach (var method in type.Methods)
                    {
                        if (type.NameSpace == NameSpace && type.Name == TypeName && method.Name == MethodName)
                        {
                            RunMethod(method, dll.Value, new CustomList<MethodArgStack>());
                            break;
                        }
                    }
                }
            }
            throw new Exception("Cannot find the method!");
        }

        internal bool InternalCallMethod(InlineMethodOperandData call, DotNetMethod m, bool addToCallStack, bool IsVirt, bool isConstructor, CustomList<MethodArgStack> stack, MethodArgStack constructorObj = null)
        {
            MethodArgStack returnValue;
            DotNetMethod m2 = null;
            if (call.RVA != 0)
            {
                //Local/Defined method
                foreach (var item2 in dlls)
                {
                    foreach (var item3 in item2.Value.Types)
                    {
                        foreach (var meth in item3.Methods)
                        {
                            var s = call.NameSpace + "." + call.ClassName;
                            if (string.IsNullOrEmpty(call.NameSpace))
                                s = call.ClassName;

                            if (meth.RVA == call.RVA && meth.Name == call.FunctionName && meth.Signature == call.Signature && meth.Parent.FullName == s)
                            {
                                m2 = meth;
                                break;
                            }
                        }
                    }
                }

                if (m2 == null)
                {
                    Console.WriteLine($"Cannot resolve called method: {call.NameSpace}.{call.ClassName}.{call.FunctionName}(). Function signature is {call.Signature}");
                    return false;
                }
            }
            else
            {
                if (call.NameSpace == "System" && call.ClassName == "Object" && call.FunctionName == ".ctor")
                {
                    return true; //Ignore
                }
                //Attempt to resolve it
                foreach (var item2 in dlls)
                {
                    foreach (var item3 in item2.Value.Types)
                    {
                        foreach (var meth in item3.Methods)
                        {
                            if (meth.Name == call.FunctionName && meth.Parent.Name == call.ClassName && meth.Parent.NameSpace == call.NameSpace && meth.Signature == call.Signature)
                            {
                                m2 = meth;
                                break;
                            }
                        }
                    }
                }
                if (m2 == null)
                {
                    clrError($"Cannot resolve method: {call.NameSpace}.{call.ClassName}.{call.FunctionName}. Method signature is {call.Signature}", "System.MethodNotFound");
                    return false;
                }
            }


            if(m2.AmountOfParms > stack.Count)
            {
                throw new Exception("Error: wrong amount of parameters supplied to function: " + m2.Name);
            }

            //for interfaces
            if (m2.RVA == 0 && !m2.IsImplementedByRuntime && !m2.IsInternalCall && m2.Parent.IsInterface)
            {
                //TODO: make sure that the object implements the interface
                var obj = stack[stack.Count - 1];
                if (obj.type != StackItemType.Object) throw new InvalidOperationException();

                m2 = null;
                foreach (var item in obj.ObjectType.Methods)
                {
                    if (item.Name == call.FunctionName)
                    {
                        m2 = item;
                    }
                }
                if (m2 == null)
                {
                    clrError("Cannot resolve method that should be implemented by the interface", "Internal CLR error");
                    return false;
                }
            }

            //Extract the params
            int StartParmIndex = stack.Count - m2.AmountOfParms;
            int EndParmIndex = stack.Count - 1;
            if (m2.AmountOfParms == 0)
            {
                StartParmIndex = -1;
                EndParmIndex = -1;
            }
            if ((EndParmIndex - StartParmIndex + 1) != m2.AmountOfParms && StartParmIndex != -1 && m2.AmountOfParms != 1)
            {
                throw new InvalidOperationException("Fatal error: an attempt was made to read " + (EndParmIndex - StartParmIndex + 1) + " parameters before calling the method, but it only needs " + m2.AmountOfParms);
            }
            CustomList<MethodArgStack> newParms = new CustomList<MethodArgStack>();
            //Find the object that we are calling it on (if any)
            MethodArgStack objectToCallOn = null;
            int objectToCallOnIndex = -1;
            if (!isConstructor && !m2.IsStatic)
            {
                if (m2.AmountOfParms >= 0)
                {
                    var idx = stack.Count - m2.AmountOfParms - 1;
                    if (idx >= 0)
                    {
                        objectToCallOn = stack[idx];
                        objectToCallOnIndex = idx;
                        bool a = false;
                        if (objectToCallOn.type == StackItemType.Object)
                        {
                            if (objectToCallOn.ObjectType != m2.Parent)
                            {
                                a = true;
                            }
                        }
                        //TODO: remove this hack
                        if (objectToCallOn.type != StackItemType.Object && idx != 0 && !IsSpecialType(objectToCallOn, m2) && a)
                        {
                            objectToCallOn = stack[idx - 1];
                            objectToCallOnIndex = idx - 1;
                            if (objectToCallOn.type == StackItemType.Object)
                            {
                                if (objectToCallOn.ObjectType != m2.Parent)
                                {
                                    objectToCallOn = null;
                                    objectToCallOnIndex = -1;
                                }
                            }
                        }
                    }
                }
            }
            if (objectToCallOn == null && IsVirt && !isConstructor)
            {
                //Try to find it
                for (int i = stack.Count - 1; i >= 0; i--)
                {
                    var itm = stack[i];
                    if (itm.type == StackItemType.Object)
                    {
                        if (itm.ObjectType == m2.Parent)
                        {
                            objectToCallOn = itm;
                            objectToCallOnIndex = i;
                            break;
                        }
                    }
                }

                if (objectToCallOn == null && !m2.IsStatic)
                {
                    throw new Exception("An attempt was made to call a virtual method with no object");
                }
            }
            if (objectToCallOn != null && m2.HasThis)
            {
                newParms.Add(objectToCallOn);
            }
            if (isConstructor)
            {
                newParms.Add(constructorObj);
            }
            if (m2.AmountOfParms == 0)
            {
                StartParmIndex = -1;
                EndParmIndex = -1;
            }
            if (StartParmIndex != -1)
            {
                for (int i5 = StartParmIndex; i5 < EndParmIndex + 1; i5++)
                {
                    var itm5 = stack[i5];
                    newParms.Add(itm5);
                }
                if (StartParmIndex == 1 && EndParmIndex == 1)
                {
                    newParms.Add(stack[StartParmIndex]);
                }
            }
            if (StartParmIndex == 0 && EndParmIndex == 0 && m2.AmountOfParms == 1)
            {
                newParms.Add(stack[0]);
            }

            //Call it
            var oldStack = stack.backend.GetRange(0, stack.Count);

            if (m != null)
                returnValue = RunMethod(m2, m.Parent.File, newParms, addToCallStack);
            else
                returnValue = RunMethod(m2, m2.Parent.File, newParms, addToCallStack);
            stack.backend = oldStack;

            //Remove the parameters once we are finished
            if (StartParmIndex != -1 && EndParmIndex != -1)
            {
                stack.RemoveRange(StartParmIndex, EndParmIndex - StartParmIndex + 1);
            }
            if (returnValue != null)
            {
                stack.Add(returnValue);
            }
            if (objectToCallOnIndex != -1)
            {
                stack.RemoveAt(objectToCallOnIndex);
            }
            return true;
        }

        private bool IsSpecialType(MethodArgStack obj, DotNetMethod m)
        {
            if (m.Parent.FullName == "System.String" && obj.type == StackItemType.String)
            {
                return true;
            }
            if (m.Parent.FullName == "System.IntPtr" && obj.type == StackItemType.IntPtr)
            {
                return true;
            }
            if (m.Parent.FullName == "System.Byte" && obj.type == StackItemType.Int32)
            {
                return true;
            }
            if (m.Parent.FullName == "System.SByte" && obj.type == StackItemType.Int32)
            {
                return true;
            }
            if (m.Parent.FullName == "System.UInt16" && obj.type == StackItemType.Int32)
            {
                return true;
            }
            if (m.Parent.FullName == "System.Int16" && obj.type == StackItemType.Int32)
            {
                return true;
            }
            if (m.Parent.FullName == "System.UInt32" && obj.type == StackItemType.Int32)
            {
                return true;
            }
            if (m.Parent.FullName == "System.Int32" && obj.type == StackItemType.Int32)
            {
                return true;
            }
            if (m.Parent.FullName == "System.UInt64" && obj.type == StackItemType.Int64)
            {
                return true;
            }
            if (m.Parent.FullName == "System.Int64" && obj.type == StackItemType.Int64)
            {
                return true;
            }
            if (m.Parent.FullName == "System.Char" && obj.type == StackItemType.Int32)
            {
                return true;
            }
            if (m.Parent.FullName == "System.Object" && obj.type == StackItemType.Object)
            {
                return true;
            }
            if (m.Parent.FullName == "System.Type" && obj.type == StackItemType.Object)
            {
                return true;
            }
            if (m.Parent.FullName == "System.Boolean" && obj.type == StackItemType.Int32)
            {
                return true;
            }
            return false;
        }
        #endregion
    }

    internal static class Arrays
    {
        public static List<ArrayRef> ArrayRefs = new List<ArrayRef>();
        private static int CurrentIndex = 0;
        public static int GetIndexFromRef(MethodArgStack r)
        {
            return (int)r.value;
        }
        public static ArrayRef AllocArray(int arrayLen)
        {
            var r = new ArrayRef();
            r.Length = arrayLen;
            r.Items = new MethodArgStack[arrayLen];
            for (int i = 0; i < arrayLen; i++)
            {
                r.Items[i] = MethodArgStack.Null();
            }
            r.Index = CurrentIndex;
            ArrayRefs.Add(r);
            CurrentIndex++;
            return ArrayRefs[CurrentIndex - 1];
        }
    }


    internal static class Objects
    {
        public static List<ObjectRef> ObjectRefs = new List<ObjectRef>();
        private static int CurrentIndex = 0;
        public static int GetIndexFromRef(MethodArgStack r)
        {
            return (int)r.value;
        }
        public static ObjectRef AllocObject()
        {
            var r = new ObjectRef();
            r.Index = CurrentIndex;
            ObjectRefs.Add(r);

            CurrentIndex++;
            return ObjectRefs[CurrentIndex - 1];
        }
    }
    internal class ObjectRef
    {
        public Dictionary<string, MethodArgStack> Fields = new Dictionary<string, MethodArgStack>();
        public int Index { get; internal set; }
    }
}
