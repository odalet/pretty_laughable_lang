#!/usr/bin/env python3

import os
import sys
import mmap
import ctypes
import struct
import platform


# TODO: function pointers
# TODO: pointer to variables
# TODO: binop8, unop8
# TODO: array
# TODO: class or struct
# TODO: node comment
# TODO: module or include directive

# TODO: register allocation
# TODO: constant prop
# TODO: tail call


def skip_space(s, idx):
    while True:
        save = idx
        # spaces
        while idx < len(s) and s[idx].isspace():
            idx += 1
        # line comment
        if idx < len(s) and s[idx] == ';':
            idx += 1
            while idx < len(s) and s[idx] != '\n':
                idx += 1
        if idx == save:
            break
    return idx


def parse_expr(s, idx):
    idx = skip_space(s, idx)
    if s[idx] == '(':
        idx += 1
        l = []
        while True:
            idx = skip_space(s, idx)
            if idx >= len(s):
                raise Exception('unbalanced parenthesis')
            if s[idx] == ')':
                idx += 1
                break
            idx, v = parse_expr(s, idx)
            l.append(v)
        return idx, l
    elif s[idx] == ')':
        raise Exception('bad parenthesis')
    elif s[idx] == '"' or s[idx] == "'":
        # string or u8
        return parse_quotes(s, idx)
    else:
        # constant or name
        start = idx
        while idx < len(s) and (not s[idx].isspace()) and s[idx] not in '()':
            idx += 1
        if start == idx:
            raise Exception('empty program')
        return idx, parse_value(s[start:idx])


def parse_quotes(s, idx):
    term = s[idx]
    end = idx + 1
    while end < len(s):
        if s[end] == term:
            break
        if s[end] == '\\':
            end += 1
        end += 1
    if end < len(s) and s[end] == term:
        # TODO: actually implement this
        import json
        v = json.loads('"' + s[idx+1:end] + '"')
        if term == '"':
            v = ['str', v]
        else:
            if len(v) != 1:
                raise Exception('bad char')
            v = ord(v)
            if not (0 <= v < 256):
                raise ValueError('bad integer range')
            v = ['val8', v]
        return end + 1, v


# a single constant, or a name
def parse_value(s):
    # int
    try:
        v = try_int(s)
    except ValueError:
        pass
    else:
        if not (-(1 << 63) <= v < (1 << 63)):
            raise ValueError('bad integer range')
        return ['val', v]

    # u8
    if s.endswith('u8'):
        try:
            v = try_int(s[:-2])
        except ValueError:
            pass
        else:
            if not (0 <= v < 256):
                raise ValueError('bad integer range')
            return ['val8', v]

    # other
    if s[0].isdigit():
        raise ValueError('bad name')
    return s


def try_int(s):
    base = 10
    if s[:2].lower() == '0x':
        base = 16
    # TODO: other bases
    return int(s, base)


def pl_parse(s):
    idx, node = parse_expr(s, 0)
    idx = skip_space(s, idx)
    if idx < len(s):
        raise ValueError('trailing garbage')
    return node


def pl_parse_main(s):
    return pl_parse('(def (main int) () (do ' + s + '))')


# the compiler state for functions
class Func:
    def __init__(self, prev):
        # the parent function (linked list)
        self.prev = prev
        # nested function level. the level of `main` is 1.
        self.level = (prev.level + 1) if prev else 0
        # the return type of this function
        self.rtype = None
        # a list of all functions. shared by all functions in a program.
        self.funcs = prev.funcs if prev else []
        # the name scope
        self.scope = Scope(None)
        # the output: a list of instructions
        self.code = []
        # current number of local variable in the stack (non-temporary)
        self.nvar = 0
        # current number of variables (both locals and temporaries)
        self.stack = 0
        # label IDs to instruction locations
        self.labels = []

    # enter a new scope
    def scope_enter(self):
        self.scope = Scope(self.scope)  # new list head
        self.scope.save = self.stack

    # exit a scope and revert the stack
    def scope_leave(self):
        self.stack = self.scope.save
        self.nvar -= self.scope.nlocal
        self.scope = self.scope.prev

    # allocate a new local variable in the current scope
    def add_var(self, name, tp):
        # add it to the map
        if name in self.scope.names:
            raise ValueError('duplicated name')
        self.scope.names[name] = (tp, self.nvar)    # (type, index)
        self.scope.nlocal += 1
        # assign the index
        assert self.stack == self.nvar
        dst = self.stack
        self.stack += 1
        self.nvar += 1
        return dst

    # lookup a name. returns a tuple of (function_level, type, index)
    def get_var(self, name):
        tp, var = scope_get_var(self.scope, name)
        if var >= 0:
            return self.level, tp, var
        if not self.prev:
            raise ValueError('undefined name')
        return self.prev.get_var(name)

    # allocate a temporary variable on the stack top and return its index
    def tmp(self):
        dst = self.stack
        self.stack += 1
        return dst

    # allocate a new label ID
    def new_label(self):
        l = len(self.labels)
        self.labels.append(None)    # filled later
        return l

    # associate the label ID to the current location
    def set_label(self, l):
        assert l < len(self.labels)
        self.labels[l] = len(self.code)


# the name scope linked list
class Scope:
    def __init__(self, prev):
        # the parent scope
        self.prev = prev
        # the number of local variables seen
        self.nlocal = 0
        # Variable names to (type, index) tuples.
        # For functions, the key includes argument types
        # and the index is the index of `Func.funcs`.
        self.names = dict()
        # the label IDs of the nearest loop
        self.loop_start = prev.loop_start if prev else -1
        self.loop_end = prev.loop_end if prev else -1


# lookup a name from a scope. returns a (type, index) tuple.
def scope_get_var(scope, name):
    while scope:    # linked list
        if name in scope.names:
            return scope.names[name]
        scope = scope.prev
    return None, -1 # not found


# the entry point of compilation.
# returns a (type, index) tuple. the index is -1 if the type is `('void',)`
def pl_comp_expr(fenv: Func, node, *, allow_var=False):
    if allow_var:
        assert fenv.stack == fenv.nvar
    save = fenv.stack

    # the actual implementation
    tp, var = pl_comp_expr_tmp(fenv, node, allow_var=allow_var)
    assert var < fenv.stack

    # Discard temporaries from the above compilation:
    if allow_var:
        # The stack is either local variables only
        fenv.stack = fenv.nvar
    else:
        # or reverts to its previous state.
        fenv.stack = save

    # The result is either a temporary stored at the top of the stack
    # or a local variable.
    assert var <= fenv.stack
    return tp, var


def pl_comp_getvar(fenv: Func, node):
    assert isinstance(node, str)
    flevel, tp, var = fenv.get_var(node)
    if flevel == fenv.level:
        # local variable
        return tp, var
    else:
        # non-local
        dst = fenv.tmp()
        fenv.code.append(('get_env', flevel, var, dst))
        return tp, dst


def pl_comp_const(fenv: Func, node):
    _, kid = node
    assert isinstance(kid, (int, str))
    dst = fenv.tmp()
    fenv.code.append(('const', kid, dst))
    tp = dict(val='int', val8='byte', str='ptr byte')[node[0]]
    tp = tuple(tp.split())
    return tp, dst


def pl_comp_binop(fenv: Func, node):
    op, lhs, rhs = node

    # compile subexpressions
    # FIXME: boolean short circuit
    save = fenv.stack
    t1, a1 = pl_comp_expr_tmp(fenv, lhs)
    t2, a2 = pl_comp_expr_tmp(fenv, rhs)
    fenv.stack = save   # discard temporaries

    # pointers
    if op == '+' and (t1[0], t2[0]) == ('int', 'ptr'):
        # rewrite `offset + ptr` into `ptr + offset`
        t1, a1, t2, a2 = t2, a2, t1, a1
    if op in '+-' and (t1[0], t2[0]) == ('ptr', 'int'):
        # ptr + offset
        scale = 8
        if t1 == ('ptr', 'byte'):
            scale = 1
        if op == '-':
            scale = -scale
        # output to a new temporary
        dst = fenv.tmp()
        fenv.code.append(('lea', a1, a2, scale, dst))
        return t1, dst
    if op == '-' and (t1[0], t2[0]) == ('ptr', 'ptr'):
        # ptr - ptr
        if t1 != t2:
            raise ValueError('comparison of different pointer types')
        if t1 != ('ptr', 'byte'):
            # TODO: ptr int
            raise NotImplementedError
        dst = fenv.tmp()
        fenv.code.append(('binop', '-', a1, a2, dst))
        return ('int',), dst

    # check types
    # TODO: allow different types
    cmp = {'eq', 'ge', 'gt', 'le', 'lt', 'ne'}
    ints = (t1 == t2 and t1[0] in ('int', 'byte'))
    ptr_cmp = (t1 == t2 and t1[0] == 'ptr' and op in cmp)
    if not (ints or ptr_cmp):
        raise ValueError('bad binop types')
    rtype = t1
    if op in cmp:
        rtype = ('int',)    # boolean

    suffix = ''
    if t1 == t2 and t1 == ('byte',):
        suffix = '8'
    # output to a new temporary
    dst = fenv.tmp()
    fenv.code.append(('binop' + suffix, op, a1, a2, dst))
    return rtype, dst


def pl_comp_unop(fenv: Func, node):
    op, arg = node
    t1, a1 = pl_comp_expr(fenv, arg)

    suffix = ''
    rtype = t1
    if op == '-':
        if t1[0] not in ('int', 'byte'):
            raise ValueError('bad unop types')
        if t1 == ('byte',):
            suffix = '8'
    elif op == 'not':
        if t1[0] not in ('int', 'byte', 'ptr'):
            raise ValueError('bad unop types')
        rtype = ('int',)    # boolean
    dst = fenv.tmp()
    fenv.code.append(('unop' + suffix, op, a1, dst))
    return rtype, dst


# The actual implementation of `pl_comp_expr`.
# This preserves temporaries while `pl_comp` discards temporaries.
def pl_comp_expr_tmp(fenv: Func, node, *, allow_var=False):
    # read a variable
    if not isinstance(node, list):
        return pl_comp_getvar(fenv, node)

    # anything else
    if len(node) == 0:
        raise ValueError('empty list')

    # constant
    if len(node) == 2 and node[0] in ('val', 'val8', 'str'):
        return pl_comp_const(fenv, node)
    # binary operators
    binops = {
        '%', '*', '+', '-', '/',
        'and', 'or',
        'eq', 'ge', 'gt', 'le', 'lt', 'ne',
    }
    if len(node) == 3 and node[0] in binops:
        return pl_comp_binop(fenv, node)
    # unary operators
    if len(node) == 2 and node[0] in {'-', 'not'}:
        return pl_comp_unop(fenv, node)
    # new scope
    if node[0] in ('do', 'then', 'else'):
        return pl_comp_scope(fenv, node)
    # new variable
    if node[0] == 'var' and len(node) == 3:
        if not allow_var:
            # Variable declarations are allowed only as
            # children of scopes and conditions.
            raise ValueError('variable declaration not allowed here')
        return pl_comp_newvar(fenv, node)
    # update a variable
    if node[0] == 'set' and len(node) == 3:
        return pl_comp_setvar(fenv, node)
    # conditional
    if len(node) in (3, 4) and node[0] in ('?', 'if'):
        return pl_comp_cond(fenv, node)
    # loop
    if node[0] == 'loop' and len(node) == 3:
        return pl_comp_loop(fenv, node)
    # break & continue
    if node == ['break']:
        if fenv.scope.loop_end < 0:
            raise ValueError('`break` outside a loop')
        fenv.code.append(('jmp', fenv.scope.loop_end))
        return ('void'), -1
    if node == ['continue']:
        if fenv.scope.loop_start < 0:
            raise ValueError('`continue` outside a loop')
        fenv.code.append(('jmp', fenv.scope.loop_start))
        return ('void'), -1
    # function call
    if node[0] == 'call' and len(node) >= 2:
        return pl_comp_call(fenv, node)
    if node[0] == 'syscall' and len(node) >= 2:
        return pl_comp_syscall(fenv, node)
    # return
    if node[0] == 'return' and len(node) in (1, 2):
        return pl_comp_return(fenv, node)
    # null pointer
    if node[0] == 'ptr':
        tp = validate_type(node)
        dst = fenv.tmp()
        fenv.code.append(('const', 0, dst))
        return tp, dst
    # cast
    if node[0] == 'cast' and len(node) == 3:
        return pl_comp_cast(fenv, node)
    # peek & poke
    if node[0] == 'peek' and len(node) == 2:
        return pl_comp_peek(fenv, node)
    if node[0] == 'poke' and len(node) == 3:
        return pl_comp_poke(fenv, node)
    # ref
    if node[0] == 'ref' and len(node) == 2:
        return pl_comp_ref(fenv, node)
    # debug
    if node == ['debug']:
        fenv.code.append(('debug',))
        return ('void',), -1

    raise ValueError('unknown expression')


def pl_comp_cast(fenv: Func, node):
    _, tp, value = node
    tp = validate_type(tp)
    val_tp, var = pl_comp_expr_tmp(fenv, value)

    # to, from
    free = [
        ('int', 'ptr'),
        ('ptr', 'int'),
        ('ptr', 'ptr'),
        ('int', 'byte'),
        ('int', 'int'),
        ('byte', 'byte'),
    ]
    if (tp[0], val_tp[0]) in free:
        return tp, var
    if (tp[0], val_tp[0]) == ('byte', 'int'):
        fenv.code.append(('cast8', var))
        return tp, var

    raise ValueError('bad cast')


def pl_comp_peek(fenv: Func, node):
    _, ptr = node
    tp, var = pl_comp_expr(fenv, ptr)
    head, *tail = tp
    tail = tuple(tail)
    if head != 'ptr':
        raise ValueError('not a pointer')
    suffix = ''
    if tail == ('byte',):
        suffix = '8'
    fenv.code.append(('peek' + suffix, var, fenv.stack))
    return tail, fenv.tmp()


def pl_comp_poke(fenv: Func, node):
    _, ptr, value = node

    save = fenv.stack
    t2, var_val = pl_comp_expr_tmp(fenv, value)
    t1, var_ptr = pl_comp_expr_tmp(fenv, ptr)
    if t1 != ('ptr', *t2):
        raise ValueError('pointer type mismatch')
    fenv.stack = save

    suffix = ''
    if t2 == ('byte',):
        suffix = '8'
    fenv.code.append(('poke' + suffix, var_ptr, var_val))
    return t2, move_to(fenv, var_val, fenv.tmp())


def pl_comp_ref(fenv: Func, node):
    _, name = node

    flevel, var_tp, var = fenv.get_var(name)
    dst = fenv.tmp()
    if flevel == fenv.level:
        fenv.code.append(('ref_var', var, dst))         # local
    else:
        fenv.code.append(('ref_env', flevel, var, dst)) # non-local
    return ('ptr', *var_tp), dst


def pl_comp_main(fenv: Func, node):
    assert node[:3] == ['def', ['main', 'int'], []]
    func = pl_scan_func(fenv, node)
    return pl_comp_func(func, node)


def pl_comp_return(fenv: Func, node):
    _, *kid = node
    tp, var = ('void',), -1
    if kid:
        tp, var = pl_comp_expr_tmp(fenv, kid[0])
    if tp != fenv.rtype:
        raise ValueError('bad return type')
    fenv.code.append(('ret', var))
    return tp, var


def pl_comp_call(fenv: Func, node):
    _, name, *args = node

    # compile arguments
    arg_types = []
    for kid in args:
        tp, var = pl_comp_expr(fenv, kid)
        arg_types.append(tp)
        move_to(fenv, var, fenv.tmp())  # stored continuously
    fenv.stack -= len(args) # points to the first argument

    # look up the target `Func`
    key = (name, tuple(arg_types))
    _, _, idx = fenv.get_var(key)
    func = fenv.funcs[idx]

    fenv.code.append(('call', idx, fenv.stack, fenv.level, func.level))
    dst = -1
    if func.rtype != ('void',):
        dst = fenv.tmp()    # the return value on the stack top
    return func.rtype, dst


def pl_comp_scope(fenv: Func, node):
    fenv.scope_enter()
    tp, var = ('void',), -1

    # split kids into groups separated by variable declarations
    groups = [[]]
    for kid in node[1:]:
        groups[-1].append(kid)
        if kid[0] == 'var':
            groups.append([])

    # Functions are visible before they are defined,
    # as long as they don't cross a variable declaration.
    # This allows adjacent functions to call each other mutually.
    for g in groups:
        # preprocess functions
        funcs = [
            pl_scan_func(fenv, kid)
            for kid in g if kid[0] == 'def' and len(kid) == 4
        ]
        # compile subexpressions
        for kid in g:
            if kid[0] == 'def' and len(kid) == 4:
                target, *funcs = funcs
                tp, var = pl_comp_func(target, kid)
            else:
                tp, var = pl_comp_expr(fenv, kid, allow_var=True)

    fenv.scope_leave()

    # the return is either a local variable or a new temporary
    if var >= fenv.stack:
        var = move_to(fenv, var, fenv.tmp())
    return tp, var


def move_to(fenv, var, dst):
    if dst != var:
        fenv.code.append(('mov', var, dst))
    return dst


def pl_comp_newvar(fenv: Func, node):
    _, name, kid = node
    # compile the initialization expression
    tp, var = pl_comp_expr(fenv, kid)
    if var < 0: # void
        raise ValueError('bad variable init type')
    # store the initialization value into the new variable
    dst = fenv.add_var(name, tp)
    return tp, move_to(fenv, var, dst)


def pl_comp_setvar(fenv: Func, node):
    _, name, kid = node

    flevel, dst_tp, dst = fenv.get_var(name)
    tp, var = pl_comp_expr(fenv, kid)
    if dst_tp != tp:
        raise ValueError('bad variable set type')

    if flevel == fenv.level:
        # local
        return dst_tp, move_to(fenv, var, dst)
    else:
        # non-local
        fenv.code.append(('set_env', flevel, dst, var))
        return dst_tp, move_to(fenv, var, fenv.tmp())


def pl_comp_cond(fenv: Func, node):
    _, cond, yes, *no = node
    l_true = fenv.new_label()   # then
    l_false = fenv.new_label()  # else
    fenv.scope_enter()  # a variable declaration is allowed on the condition

    # the condition expression
    tp, var = pl_comp_expr(fenv, cond, allow_var=True)
    if tp == ('void',):
        raise ValueError('expect boolean condition')
    fenv.code.append(('jmpf', var, l_false))    # go to `else` if false

    # then
    t1, a1 = pl_comp_expr(fenv, yes)
    if a1 >= 0:
        # Both `then` and `else` goes to the same variable,
        # thus a temporary is needed.
        move_to(fenv, a1, fenv.stack)

    # else, optional
    t2, a2 = ('void',), -1
    if no:
        fenv.code.append(('jmp', l_true))   # skip `else` after `then`
    fenv.set_label(l_false)
    if no:
        t2, a2 = pl_comp_expr(fenv, no[0])
        if a2 >= 0:
            move_to(fenv, a2, fenv.stack)   # the same variable for `then`
    fenv.set_label(l_true)

    fenv.scope_leave()
    if a1 < 0 or a2 < 0 or t1 != t2:
        return ('void',), -1    # different types, no return value
    else:
        return t1, fenv.tmp()   # allocate the temporary for the result


def pl_comp_loop(fenv: Func, node):
    _, cond, body = node
    fenv.scope.loop_start = fenv.new_label()
    fenv.scope.loop_end = fenv.new_label()

    # enter
    fenv.scope_enter()  # allow_var=True
    fenv.set_label(fenv.scope.loop_start)
    # cond
    _, var = pl_comp_expr(fenv, cond, allow_var=True)
    if var < 0: # void
        raise ValueError('bad condition type')
    fenv.code.append(('jmpf', var, fenv.scope.loop_end))
    # body
    _, _ = pl_comp_expr(fenv, body)
    # loop
    fenv.code.append(('jmp', fenv.scope.loop_start))
    # leave
    fenv.set_label(fenv.scope.loop_end)
    fenv.scope_leave()

    return ('void',), -1


# check for accepted types. returns a tuple.
def validate_type(tp):
    if len(tp) == 0:
        raise ValueError('type missing')
    head, *body = tp
    if head == 'ptr':
        body = validate_type(body)
        if body == ('void',):
            raise ValueError('bad pointer element')
    elif head in ('void', 'int', 'byte'):
        if body:
            raise ValueError('bad scalar type')
    else:
        raise ValueError('unknown type')
    return (head, *body)


# function preprocessing:
# make the function visible to the whole scope before its definition.
def pl_scan_func(fenv: Func, node):
    _, (name, *rtype), args, _ = node
    rtype = validate_type(rtype)

    # add the (name, arg-types) pair to the map
    arg_type_list = tuple(validate_type(arg_type) for _, *arg_type in args)
    key = (name, arg_type_list) # allows overloading by argument types
    if key in fenv.scope.names:
        raise ValueError('duplicated function')
    fenv.scope.names[key] = (rtype, len(fenv.funcs))

    # the new function
    func = Func(fenv)
    func.rtype = rtype
    fenv.funcs.append(func)
    return func


# actually compile the function definition.
# note that the `fenv` argument is the target function!
def pl_comp_func(fenv: Func, node):
    _, _, args, body = node

    # treat arguments as local variables
    for arg_name, *arg_type in args:
        if not isinstance(arg_name, str):
            raise ValueError('bad argument name')
        arg_type = validate_type(arg_type)
        if arg_type == ('void',):
            raise ValueError('bad argument type')
        fenv.add_var(arg_name, arg_type)
    assert fenv.stack == len(args)

    # compile the function body
    body_type, var = pl_comp_expr(fenv, body)
    if fenv.rtype != ('void',) and fenv.rtype != body_type:
        raise ValueError('bad body type')
    if fenv.rtype == ('void',):
        var = -1
    fenv.code.append(('ret', var))  # the implicit return
    return ('void',), -1


def pl_comp_syscall(fenv: Func, node):
    _, num, *args = node
    if isinstance(num, list) and num[0] == 'val':
        _, num = num
    if not isinstance(num, int) or num < 0:
        raise ValueError('bad syscall number')

    save = fenv.stack
    sys_vars = []
    for kid in args:
        arg_tp, var = pl_comp_expr_tmp(fenv, kid)
        if arg_tp == ('void',):
            raise ValueError('bad syscall argument type')
        sys_vars.append(var)
    fenv.stack = save

    fenv.code.append(('syscall', fenv.stack, num, *sys_vars))
    return ('int',), fenv.tmp()


# execute the program as a ctype function
class MemProgram:
    def __init__(self, code):
        # copy the code to an executable memory buffer
        flags = mmap.MAP_PRIVATE|mmap.MAP_ANONYMOUS
        prot = mmap.PROT_EXEC|mmap.PROT_READ|mmap.PROT_WRITE
        self.code = mmap.mmap(-1, len(code), flags=flags, prot=prot)
        self.code[:] = code

        # ctype function: int64_t (*)(void *stack)
        func_type = ctypes.CFUNCTYPE(ctypes.c_int64, ctypes.c_void_p)
        cbuf = ctypes.c_void_p.from_buffer(self.code)
        self.cfunc = func_type(ctypes.addressof(cbuf))

        # create the data stack
        flags = mmap.MAP_PRIVATE|mmap.MAP_ANONYMOUS
        prot = mmap.PROT_READ|mmap.PROT_WRITE
        self.stack = mmap.mmap(-1, 8 << 20, flags=flags, prot=prot)
        cbuf = ctypes.c_void_p.from_buffer(self.stack)
        self.stack_addr = ctypes.addressof(cbuf)
        # TODO: mprotect

    def invoke(self):
        return self.cfunc(self.stack_addr)

    def close(self):
        self.code.close()
        self.stack.close()


# execute the program as a ctype function
class MemProgramWindows:
    def __init__(self, code):
        self.kernel32 = ctypes.CDLL('kernel32', use_last_error=True)

        MEM_COMMIT = 0x00001000
        MEM_RESERVE = 0x00002000
        PAGE_READWRITE = 0x04
        PAGE_EXECUTE_READWRITE = 0x40

        VirtualAlloc = self.kernel32.VirtualAlloc
        VirtualAlloc.restype = ctypes.c_void_p

        # copy the code to an executable memory buffer
        self.code = VirtualAlloc(
            None, len(code),
            MEM_COMMIT | MEM_RESERVE,
            PAGE_EXECUTE_READWRITE,
        )
        cbuf = ctypes.c_void_p.from_buffer(code)
        ctypes.memmove(self.code, ctypes.addressof(cbuf), len(code))

        # ctype function: int64_t (*)(void *stack)
        func_type = ctypes.CFUNCTYPE(ctypes.c_int64, ctypes.c_void_p)
        self.cfunc = func_type(self.code)

        # create the data stack
        self.stack = VirtualAlloc(
            None, 8 << 20,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_READWRITE,
        )
        # TODO: mprotect

    def invoke(self):
        return self.cfunc(self.stack)

    def close(self):
        MEM_RELEASE = 0x00008000

        VirtualFree = self.kernel32.VirtualFree
        VirtualFree.argtypes = (ctypes.c_void_p, ctypes.c_size_t, ctypes.c_int)
        VirtualFree.restype = ctypes.c_bool

        ok = VirtualFree(self.code, 0, MEM_RELEASE)
        assert ok
        ok = VirtualFree(self.stack, 0, MEM_RELEASE)
        assert ok


# ELF dissambler:
# objdump -b binary -M intel,x86-64 -m i386 \
#   --adjust-vma=0x1000 --start-address=0x1080 -D ELF_FILE
class CodeGen:
    # register encodings
    A = 0
    C = 1
    D = 2
    B = 3
    SP = 4
    BP = 5
    SI = 6
    DI = 7

    def __init__(self):
        # params
        self.vaddr = 0x1000     # the virtual address for the program
        self.alignment = 16
        # output
        self.buf = bytearray()
        # states
        self.jmps = dict()      # label -> offset list
        self.calls = dict()     # function index -> offset list
        self.strings = dict()   # string literal -> offset list
        self.func2off = []      # func idx -> offset
        self.fields = dict()    # ELF field name -> (size, offset)

    # append a placeholder field
    def f16(self, name):
        self.fields[name] = (2, len(self.buf))
        self.buf.extend(b'\0\0')
    def f32(self, name):
        self.fields[name] = (4, len(self.buf))
        self.buf.extend(b'\0\0\0\0')
    def f64(self, name):
        self.fields[name] = (8, len(self.buf))
        self.buf.extend(b'\0' * 8)

    # fill in the placeholder
    def setf(self, name, i):
        sz, off = self.fields[name]
        fmt = {2: '<H', 4: '<I', 8: '<Q'}[sz]
        self.buf[off:off+sz] = struct.pack(fmt, i)

    def elf_begin(self):
        self.elf_header()

        phdr_start = len(self.buf)  # the program header starts here
        self.elf_program_header()
        # program header size
        self.setf('e_phentsize', len(self.buf) - phdr_start)
        # number of program headers: 1
        self.setf('e_phnum', 1)

        self.padding()
        # the entry point: the virtual address where the program start
        self.setf('e_entry', self.vaddr + len(self.buf))

    def elf_header(self):
        # ref: https://www.muppetlabs.com/~breadbox/software/tiny/tiny-elf64.asm.txt
        self.buf.extend(bytes.fromhex('7F 45 4C 46 02 01 01 00'))
        self.buf.extend(bytes.fromhex('00 00 00 00 00 00 00 00'))
        # e_type, e_machine, e_version
        self.buf.extend(bytes.fromhex('02 00 3E 00 01 00 00 00'))
        self.f64('e_entry')
        self.f64('e_phoff')
        self.f64('e_shoff')
        self.f32('e_flags')
        self.f16('e_ehsize')
        self.f16('e_phentsize')
        self.f16('e_phnum')
        self.f16('e_shentsize')
        self.f16('e_shnum')
        self.f16('e_shstrndx')
        self.setf('e_phoff', len(self.buf))     # offset of the program header
        self.setf('e_ehsize', len(self.buf))    # size of the ELF header

    def elf_program_header(self):
        # p_type, p_flags
        self.buf.extend(bytes.fromhex('01 00 00 00 05 00 00 00'))
        # p_offset
        self.i64(0)
        # p_vaddr, p_paddr
        self.i64(self.vaddr)
        self.i64(self.vaddr)    # useless
        self.f64('p_filesz')
        self.f64('p_memsz')
        # p_align
        self.i64(0x1000)

    # compile the program to an ELF executable
    def output_elf(self, root: Func):
        # ELF header + program header
        self.elf_begin()
        # machine code
        self.code_entry()
        for func in root.funcs:
            self.func(func)
        self.code_end()
        # fill in some ELF fields
        self.elf_end()

    def elf_end(self):
        # fields in program header:
        # the size of the mapping. we're mapping the whole file here.
        self.setf('p_filesz', len(self.buf))
        self.setf('p_memsz', len(self.buf))

    def create_stack(self, data):
        def operand(i):
            return struct.pack('<i', i)

        # syscall ref: https://blog.rchapman.org/posts/Linux_System_Call_Table_for_x86_64/
        # syscall abi: https://github.com/torvalds/linux/blob/v5.0/arch/x86/entry/entry_64.S#L107
        # mmap
        self.buf.extend(
            b"\xb8\x09\x00\x00\x00"     # mov eax, 9
            # b"\x31\xff"                 # xor edi, edi      // addr = NULL
            b"\xbf\x00\x10\x00\x00"     # mov edi, 4096     // addr
            b"\x48\xc7\xc6%s"           # mov rsi, xxx      // len
            b"\xba\x03\x00\x00\x00"     # mov edx, 3        // prot = PROT_READ|PROT_WRITE
            b"\x41\xba\x22\x00\x00\x00" # mov r10d, 0x22    // flags = MAP_PRIVATE|MAP_ANONYMOUS
            b"\x49\x83\xc8\xff"         # or r8, -1         // fd = -1
            b"\x4d\x31\xc9"             # xor r9, r9        // offset = 0
            b"\x0f\x05"                 # syscall
            b"\x48\x89\xc3"             # mov rbx, rax      // the data stack
            % operand(data + 4096)
        )

        # mprotect
        self.buf.extend(
            b"\xb8\x0a\x00\x00\x00"     # mov eax, 10
            b"\x48\x8d\xbb%s"           # lea rdi, [rbx + data]
            b"\xbe\x00\x10\x00\x00"     # mov esi, 4096
            b"\x31\xd2"                 # xor edx, edx
            b"\x0f\x05"                 # syscall
            % operand(data)
        )
        # FIXME: check the syscall return value

    def code_entry(self):
        # create the data stack (8M)
        self.create_stack(0x800000)
        # call the main function
        self.asm_call(0)
        # exit
        self.buf.extend(
            b"\xb8\x3c\x00\x00\x00"     # mov eax, 60
            b"\x48\x8b\x3b"             # mov rdi, [rbx]
            b"\x0f\x05"                 # syscall
        )

    # easier to find things in hexdump
    def padding(self):
        if self.alignment == 0:
            return
        self.buf.append(0xcc)   # int3
        while len(self.buf) % self.alignment:
            self.buf.append(0xcc)

    # compile to a callable function
    def output_mem(self, root: Func):
        self.mem_entry()
        for func in root.funcs:
            self.func(func)
        self.code_end()

    # C function: int64_t (*)(void *stack)
    def mem_entry(self):
        # the first argument is the data stack
        self.buf.extend(b"\x53")            # push rbx
        system = platform.system()
        if system == 'Windows' or system.startswith('CYGWIN'):
            self.buf.extend(b"\x48\x89\xCB")    # mov rbx, rcx
        else:
            self.buf.extend(b"\x48\x89\xFB")    # mov rbx, rdi
        # call the main function
        self.asm_call(0)
        # the return value
        self.buf.extend(b"\x48\x8b\x03")    # mov rax, [rbx]
        self.buf.extend(b"\x5b")            # pop rbx
        self.buf.extend(b"\xc3")            # ret

    # compile a function
    def func(self, func: Func):
        self.padding()

        # offsets
        self.func2off.append(len(self.buf)) # function index -> code offset
        pos2off = []    # virtual instruction -> code offset

        # call the method for each instruction
        for instr_name, *instr_args in func.code:
            pos2off.append(len(self.buf))
            method = getattr(self.__class__, instr_name)
            method(self, *instr_args)

        # fill in the jmp address
        for L, off_list in self.jmps.items():
            dst_off = pos2off[func.labels[L]]
            for patch_off in off_list:
                self.patch_addr(patch_off, dst_off)
        self.jmps.clear()

    # fill in a 4-byte `rip` relative offset
    def patch_addr(self, patch_off, dst_off):
        src_off = patch_off + 4     # rip
        relative = struct.pack('<i', dst_off - src_off)
        self.buf[patch_off:patch_off+4] = relative

    def code_end(self):
        # fill in the call address
        for L, off_list in self.calls.items():
            dst_off = self.func2off[L]
            for patch_off in off_list:
                self.patch_addr(patch_off, dst_off)
        self.calls.clear()
        self.padding()
        # strings
        for s, off_list in self.strings.items():
            dst_off = len(self.buf)
            for patch_off in off_list:
                self.patch_addr(patch_off, dst_off)
            self.buf.extend(s.encode('utf-8') + b'\0')
        self.strings.clear()

    # append a signed integer
    def i8(self, i):
        self.buf.append(i if i >= 0 else (256 + i))
    def i32(self, i):
        self.buf.extend(struct.pack('<i', i))
    def i64(self, i):
        self.buf.extend(struct.pack('<q', i))

    # instr reg, [rm + disp]
    # instr [rm + disp], reg
    def asm_disp(self, lead, reg, rm, disp):
        assert reg < 16 and rm < 16 and rm != CodeGen.SP

        lead = bytearray(lead)  # optional prefix + opcode
        if reg >= 8 or rm >= 8:
            assert (lead[0] >> 4) == 0b0100 # REX
            lead[0] |= (reg >> 3) << 2      # REX.R
            lead[0] |= (rm >> 3) << 0       # REX.B
            reg &= 0b111
            rm &= 0b111

        self.buf.extend(lead)
        if disp == 0:
            mod = 0     # [rm]
        elif -128 <= disp < 128:
            mod = 1     # [rm + disp8]
        else:
            mod = 2     # [rm + disp32]
        self.buf.append((mod << 6) | (reg << 3) | rm)  # ModR/M
        if mod == 1:
            self.i8(disp)
        if mod == 2:
            self.i32(disp)

    # mov reg, [rm + disp]
    def asm_load(self, reg, rm, disp):
        self.asm_disp(b'\x48\x8b', reg, rm, disp)

    # mov [rm + disp], reg
    def asm_store(self, rm, disp, reg):
        self.asm_disp(b'\x48\x89', reg, rm, disp)

    def store_rax(self, dst):
        # mov [rbx + dst*8], rax
        self.asm_store(CodeGen.B, dst * 8, CodeGen.A)

    def load_rax(self, src):
        # mov rax, [rbx + src*8]
        self.asm_load(CodeGen.A, CodeGen.B, src * 8)

    def const(self, val, dst):
        assert isinstance(val, (int, str))
        if isinstance(val, str):
            # lea rax, [rip + offset]
            self.buf.extend(b"\x48\x8d\x05")
            self.strings.setdefault(val, []).append(len(self.buf))
            self.buf.extend(b"\0\0\0\0")
        elif val == 0:
            self.buf.extend(b"\x31\xc0")            # xor eax, eax
        elif val == -1:
            self.buf.extend(b"\x48\x83\xc8\xff")    # or rax, -1
        elif (val >> 31) == 0:
            self.buf.extend(b"\xb8")                # mov eax, imm32
            self.i32(val)
        elif (val >> 31) == -1:
            # sign-extended
            self.buf.extend(b"\x48\xc7\xc0")        # mov rax, imm32
            self.i32(val)
        else:
            self.buf.extend(b"\x48\xb8")            # mov rax, imm64
            self.i64(val)
        self.store_rax(dst)

    def mov(self, src, dst):
        if src == dst:
            return
        self.load_rax(src)
        self.store_rax(dst)

    def binop(self, op, a1, a2, dst):
        self.load_rax(a1)

        arith = {
            '+': b'\x48\x03',       # add  reg, rm
            '-': b'\x48\x2b',       # sub  reg, rm
            '*': b'\x48\x0f\xaf',   # imul reg, rm
        }
        cmp = {
            'eq': b'\x0f\x94\xc0',  # sete  al
            'ne': b'\x0f\x95\xc0',  # setne al
            'ge': b'\x0f\x9d\xc0',  # setge al
            'gt': b'\x0f\x9f\xc0',  # setg  al
            'le': b'\x0f\x9e\xc0',  # setle al
            'lt': b'\x0f\x9c\xc0',  # setl  al
        }

        if op in ('/', '%'):
            # xor edx, edx
            self.buf.extend(b"\x31\xd2")
            # idiv rax, [rbx + a2*8]
            self.buf.extend(b'\x48\xf7\xbb')
            self.i32(a2 * 8)
            if op == '%':
                # mov rax, rdx
                self.buf.extend(b"\x48\x89\xd0")
        elif op in arith:
            # op rax, [rbx + a2*8]
            self.asm_disp(arith[op], CodeGen.A, CodeGen.B, a2 * 8)
        elif op in cmp:
            # cmp rax, [rbx + a2*8]
            self.asm_disp(b'\x48\x3b', CodeGen.A, CodeGen.B, a2 * 8)
            # setcc al
            self.buf.extend(cmp[op])
            # movzx eax, al
            self.buf.extend(b"\x0f\xb6\xc0")
        elif op == 'and':
            self.buf.extend(
                b"\x48\x85\xc0"     # test rax, rax
                b"\x0f\x95\xc0"     # setne al
            )
            # mov rdx, [rbx + a2*8]
            self.asm_load(CodeGen.D, CodeGen.B, a2 * 8)
            self.buf.extend(
                b"\x48\x85\xd2"     # test rdx, rdx
                b"\x0f\x95\xc2"     # setne dl
                b"\x21\xd0"         # and eax, edx
                b"\x0f\xb6\xc0"     # movzx eax, al
            )
        elif op == 'or':
            # or rax, [rbx + a2*8]
            self.asm_disp(b"\x48\x0b", CodeGen.A, CodeGen.B, a2 * 8)
            self.buf.extend(
                b"\x0f\x95\xc0"     # setne al
                b"\x0f\xb6\xc0"     # movzx eax, al
            )
        else:
            raise NotImplementedError

        self.store_rax(dst)

    def unop(self, op, a1, dst):
        self.load_rax(a1)
        if op == '-':
            self.buf.extend(b"\x48\xf7\xd8")    # neg rax
        elif op == 'not':
            self.buf.extend(
                b"\x48\x85\xc0"     # test rax, rax
                b"\x0f\x94\xc0"     # sete al
                b"\x0f\xb6\xc0"     # movzx eax, al
            )
        else:
            raise NotImplementedError
        self.store_rax(dst)

    def jmpf(self, a1, L):
        self.load_rax(a1)
        self.buf.extend(
            b"\x48\x85\xc0"         # test rax, rax
            b"\x0f\x84"             # je
        )
        self.jmps.setdefault(L, []).append(len(self.buf))
        self.buf.extend(b'\0\0\0\0')

    def jmp(self, L):
        self.buf.extend(b"\xe9")    # jmp
        self.jmps.setdefault(L, []).append(len(self.buf))
        self.buf.extend(b'\0\0\0\0')

    def asm_call(self, L):
        self.buf.extend(b"\xe8")    # call
        self.calls.setdefault(L, []).append(len(self.buf))
        self.buf.extend(b'\0\0\0\0')

    def call(self, func, arg_start, level_cur, level_new):
        assert 1 <= level_cur
        assert 1 <= level_new <= level_cur + 1

        # put a list of pointers to outer frames in the `rsp` stack
        if level_new > level_cur:
            # grow the list by one
            self.buf.append(0x53)               # push rbx
        for _ in range(min(level_new, level_cur) - 1):
            # copy the previous list
            self.buf.extend(b"\xff\xb4\x24")    # push [rsp + (level_new-1)*8]
            self.i32((level_new - 1) * 8)

        # make a new frame and call the target
        if arg_start != 0:
            self.buf.extend(b"\x48\x81\xc3")    # add rbx, arg_start*8
            self.i32(arg_start * 8)
        self.asm_call(func)                     # call func
        if arg_start != 0:
            self.buf.extend(b"\x48\x81\xc3")    # add rbx, -arg_start*8
            self.i32(-arg_start * 8)

        # discard the list of pointers
        self.buf.extend(b"\x48\x81\xc4")        # add rsp, (level_new - 1)*8
        self.i32((level_new - 1) * 8)

    def ret(self, a1):
        if a1 > 0:
            self.load_rax(a1)
            self.store_rax(0)
        self.buf.append(0xc3)       # ret

    def load_env_addr(self, level_var):
        self.buf.extend(b"\x48\x8b\x84\x24")    # mov rax, [rsp + level_var*8]
        self.i32(level_var * 8)

    def get_env(self, level_var, var, dst):
        self.load_env_addr(level_var)
        # mov rax, [rax + var*8]
        self.asm_load(CodeGen.A, CodeGen.A, var * 8)
        # mov [rbx + dst*8], rax
        self.store_rax(dst)

    def set_env(self, level_var, var, src):
        self.load_env_addr(level_var)
        # mov rdx, [rbx + src*8]
        self.asm_load(CodeGen.D, CodeGen.B, src * 8)
        # mov [rax + var*8], rdx
        self.asm_store(CodeGen.A, var * 8, CodeGen.D)

    def lea(self, a1, a2, scale, dst):
        self.load_rax(a1)
        self.asm_load(CodeGen.D, CodeGen.B, a2 * 8) # mov rdx, [rbx + a2*8]
        if scale < 0:
            self.buf.extend(b"\x48\xf7\xda")        # neg rdx
        self.buf.extend({
            1: b"\x48\x8d\x04\x10",                 # lea rax, [rax + rdx]
            2: b"\x48\x8d\x04\x50",                 # lea rax, [rax + rdx*2]
            4: b"\x48\x8d\x04\x90",                 # lea rax, [rax + rdx*4]
            8: b"\x48\x8d\x04\xd0",                 # lea rax, [rax + rdx*8]
        }[abs(scale)])
        self.store_rax(dst)

    def peek(self, var, dst):
        self.load_rax(var)
        # mov rax, [rax]
        self.asm_load(CodeGen.A, CodeGen.A, 0)
        self.store_rax(dst)

    def peek8(self, var, dst):
        self.load_rax(var)
        # movzx eax, byte ptr [rax]
        self.buf.extend(b"\x0f\xb6\x00")
        self.store_rax(dst)

    def poke(self, ptr, val):
        self.load_rax(val)
        # mov rdx, [rbx + ptr*8]
        self.asm_load(CodeGen.D, CodeGen.B, ptr * 8)
        # mov [rdx], rax
        self.asm_store(CodeGen.D, 0, CodeGen.A)

    def poke8(self, ptr, val):
        self.load_rax(val)
        # mov rdx, [rbx + ptr*8]
        self.asm_load(CodeGen.D, CodeGen.B, ptr * 8)
        # mov [rdx], al
        self.buf.extend(b"\x88\x02")

    def ref_var(self, var, dst):
        # lea rax, [rbx + var*8]
        self.buf.extend(b"\x48\x8D\x83")
        self.i32(var * 8)
        self.store_rax(dst)

    def ref_env(self, level_var, var, dst):
        # mov rax, [rsp + level_var*8]
        self.load_env_addr(level_var)
        # add rax, var*8
        self.buf.extend(b"\x48\x05")
        self.i32(var * 8)
        self.store_rax(dst)

    def cast8(self, var):
        # and qword ptr [rbx + var*8], 0xff
        self.asm_disp(b"\x48\x81", 4, CodeGen.B, var * 8)
        self.i32(0xff)

    def syscall(self, dst, num, *arg_list):
        # syscall ref: https://blog.rchapman.org/posts/Linux_System_Call_Table_for_x86_64/
        self.buf.extend(b"\xb8")                # mov eax, imm32
        self.i32(num)
        arg_regs = [CodeGen.DI, CodeGen.SI, CodeGen.D, 10, 8, 9]
        assert len(arg_list) <= len(arg_regs)
        for i, arg in enumerate(arg_list):
            # mov reg, [rbx + arg*8]
            self.asm_load(arg_regs[i], CodeGen.B, arg * 8)
        self.buf.extend(b"\x0f\x05")            # syscall
        self.store_rax(dst)                     # mov [rbx + dst*8], rax

    def debug(self):
        self.buf.append(0xcc)                   # int3


# ir
'''
const val dst
mov src dst
binop op a1 a2 dst
unop op a1 dst
binop8 op a1 a2 dst
unop8 op a1 dst
jmpf a1 L
jmp L
ret a1
ret -1
call func arg_start level_cur level_new
get_env level_var var dst
set_env level_var var src
ref_var var dst
ref_env level_var var dst
lea
peek
poke
peek8
poke8
cast8
syscall
debug
'''


# syntax
'''
(+ a b)
(- a b)
(* a b)
(/ a b)
...

(eq a b)
(ne a b)
(ge a b)
(gt a b)
(le a b)
(lt a b)

(not b)
(and a b)
(or a b)

(? cond yes no)
(if cond (then yes blah blah) (else no no no))
(do a b c...)
(var name val)
(set name val)
(loop cond body)
(break)
(continue)

(def (name rtype) ((a1 a1type) (a2 a2type)...) body)
(call f a b c...)
(return val)

(ptr elem_type)
(peek ptr)
(poke ptr value)
(ref name)
(syscall num args...)
(cast type val)
'''


# types
'''
void
int
byte
ptr int
ptr byte
'''


def ir_dump(root: Func):
    out = []
    for i, func in enumerate(root.funcs):
        out.append(f'func{i}:')
        pos2labels = dict()
        for label, pos in enumerate(func.labels):
            pos2labels.setdefault(pos, []).append(label)
        for pos, instr in enumerate(func.code):
            for label in pos2labels.get(pos, []):
                out.append(f'L{label}:')
            if instr[0].startswith('jmp'):
                instr = instr[:-1] + (f'L{instr[-1]}',)
            if instr[0] == 'const' and isinstance(instr[1], str):
                import json
                instr = list(instr)
                instr[1] = json.dumps(instr[1])
            out.append('    ' + ' '.join(map(str, instr)))
        out.append('')

    return '\n'.join(out)


def test_comp():
    def f(s):
        node = pl_parse_main(s)
        fenv = Func(None)
        pl_comp_main(fenv, node)
        return [x.code for x in fenv.funcs]

    def asm(s):
        node = pl_parse_main(s)
        fenv = Func(None)
        pl_comp_main(fenv, node)
        return ir_dump(fenv)

    assert f('1') == [[
        ('const', 1, 0),
        ('ret', 0),
    ]]
    assert f('1 3') == [[
        ('const', 1, 0),
        ('const', 3, 0),
        ('ret', 0),
    ]]
    assert f('(+ (- 1 2) 3)') == [[
        ('const', 1, 0),
        ('const', 2, 1),
        ('binop', '-', 0, 1, 0),
        ('const', 3, 1),
        ('binop', '+', 0, 1, 0),
        ('ret', 0),
    ]]
    assert f('(return 1)') == [[
        ('const', 1, 0),
        ('ret', 0),
        ('ret', 0),
    ]]
    assert asm('(if 1 2 3)').split() == '''
        func0:
            const 1 0
            jmpf 0 L1
            const 2 0
            jmp L0
        L1:
            const 3 0
        L0:
            ret 0
    '''.split()
    assert asm('''
        (loop (var a 1) (do
            (var b a)
            (if (gt a 11)
                (break))
            (var c (set a (+ 2 b)))
            (if (lt c 100)
                (continue))
            (set b 5)
        ))
        0''').split() == '''
        func0:
        L0:
            const 1 0
            jmpf 0 L1
            mov 0 1
            const 11 2
            binop gt 0 2 2
            jmpf 2 L3
            jmp L1
        L2:
        L3:
            const 2 2
            binop + 2 1 2
            mov 2 0
            mov 0 2
            const 100 3
            binop lt 2 3 3
            jmpf 3 L5
            jmp L0
        L4:
        L5:
            const 5 3
            mov 3 1
            jmp L0
        L1:
            const 0 0
            ret 0
    '''.split()
    assert asm('(if 1 (return 2)) 0').split() == '''
        func0:
            const 1 0
            jmpf 0 L1
            const 2 0
            ret 0
        L0:
        L1:
            const 0 0
            ret 0
    '''.split()
    assert asm('(var a 1) (set a (+ 3 a)) (var b 2) (- b a)').split() == '''
        func0:
            const 1 0
            const 3 1
            binop + 1 0 1
            mov 1 0
            const 2 1
            binop - 1 0 2
            mov 2 0
            ret 0
    '''.split()
    assert asm('(var a 1) (return (+ 3 a))').split() == '''
        func0:
            const 1 0
            const 3 1
            binop + 1 0 1
            ret 1
            mov 1 0
            ret 0
    '''.split()
    assert asm('(var a 1) (+ 3 a)').split() == '''
        func0:
            const 1 0
            const 3 1
            binop + 1 0 1
            mov 1 0
            ret 0
    '''.split()
    assert asm('''
        (def (fib int) ((n int)) (if (le n 0) (then 0) (else (call fib (- n 1)))))
        (call fib 5)
        ''').split() == '''
        func0:
            const 5 0
            call 1 0 1 2
            ret 0
        func1:
            const 0 1
            binop le 0 1 1
            jmpf 1 L1
            const 0 1
            jmp L0
        L1:
            const 1 1
            binop - 0 1 1
            call 1 1 2 2
        L0:
            ret 1
    '''.split()
    assert asm('''
        (var b 456)
        (def (f void) () (do
            (var a 123)
            (def (g void) () (do
                (set a (+ b a))
            ))
            (call g)
        ))

        (call f)
        0
        ''').split() == '''
        func0:
            const 456 0
            call 1 1 1 2
            const 0 1
            mov 1 0
            ret 0
        func1:
            const 123 0
            call 2 1 2 3
            ret -1
        func2:
            get_env 1 0 0
            get_env 2 0 1
            binop + 0 1 0
            set_env 2 0 0
            ret -1
    '''.split()
    assert asm('''
        (var p (ptr int))
        (poke (cast (ptr byte) p) 124u8)
        (peek (cast (ptr byte) p))
        (poke p 123)
    ''').split() == '''
        func0:
            const 0 0
            const 124 1
            poke8 0 1
            peek8 0 1
            const 123 1
            poke 0 1
            mov 1 0
            ret 0
    '''.split()


def main():
    # args
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument('file', nargs='?', help='the input source file')
    ap.add_argument('--exec', action='store_true', help='compile to memory and execute it')
    ap.add_argument('-o', '--output', help='the output path')
    ap.add_argument('--print-ir', action='store_true', help='print the intermediate representation')
    ap.add_argument('--alignment', type=int, default=16)
    args = ap.parse_args()
    if not (args.file or args.output or args.exec):
        ap.print_help()
        test_comp()
        return

    # source text
    with open(args.file, 'rt', encoding='utf-8') as fp:
        text = fp.read()

    # parse & compile
    node = pl_parse_main(text)
    root = Func(None)
    _ = pl_comp_main(root, node)
    if args.print_ir:
        print(ir_dump(root))

    # generate output
    if args.output:
        gen = CodeGen()
        gen.alignment = args.alignment
        gen.output_elf(root)
        fd = os.open(args.output, os.O_WRONLY|os.O_CREAT|os.O_TRUNC, 0o755)
        with os.fdopen(fd, 'wb', closefd=True) as fp:
            fp.write(gen.buf)

    # execute
    if args.exec:
        gen = CodeGen()
        gen.alignment = args.alignment
        gen.output_mem(root)
        if platform.system() == 'Windows':
            prog = MemProgramWindows(gen.buf)
        else:
            prog = MemProgram(gen.buf)
        try:
            sys.exit(prog.invoke())
        finally:
            prog.close()


if __name__ == '__main__':
    main()
