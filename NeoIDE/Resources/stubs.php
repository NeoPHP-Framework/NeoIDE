<?php
// Dumps the internal functions/classes/constants of this PHP runtime as JSON (used by NeoIDE for completion).
error_reporting(0);

function neo_type($t) {
    if ($t === null) return null;
    return (string) $t;
}

function neo_params(ReflectionFunctionAbstract $f) {
    $out = [];
    foreach ($f->getParameters() as $p) {
        $item = ['n' => $p->getName()];
        $t = neo_type($p->getType());
        if ($t !== null) $item['t'] = $t;
        if ($p->isOptional()) $item['o'] = 1;
        if ($p->isVariadic()) $item['v'] = 1;
        if ($p->isPassedByReference()) $item['r'] = 1;
        if ($p->isDefaultValueAvailable()) {
            try {
                $item['d'] = $p->isDefaultValueConstant() ? $p->getDefaultValueConstantName() : var_export($p->getDefaultValue(), true);
            } catch (Throwable $e) {}
        }
        $out[] = $item;
    }
    return $out;
}

function neo_return(ReflectionFunctionAbstract $f) {
    if ($f->hasReturnType()) return neo_type($f->getReturnType());
    if (method_exists($f, 'hasTentativeReturnType') && $f->hasTentativeReturnType()) return neo_type($f->getTentativeReturnType());
    return null;
}

$result = ['version' => PHP_VERSION, 'functions' => [], 'classes' => [], 'constants' => []];

foreach (get_defined_functions()['internal'] as $name) {
    try {
        $f = new ReflectionFunction($name);
        $item = ['n' => $f->getName(), 'p' => neo_params($f)];
        $r = neo_return($f);
        if ($r !== null) $item['r'] = $r;
        if ($f->isDeprecated()) $item['dep'] = 1;
        $result['functions'][] = $item;
    } catch (Throwable $e) {}
}

$all = array_merge(get_declared_classes(), get_declared_interfaces(), get_declared_traits());
foreach ($all as $cn) {
    try {
        $c = new ReflectionClass($cn);
        if (!$c->isInternal()) continue;
        $kind = $c->isInterface() ? 'interface' : ($c->isTrait() ? 'trait' : (method_exists($c, 'isEnum') && $c->isEnum() ? 'enum' : 'class'));
        $item = ['n' => $c->getName(), 'k' => $kind];
        if ($c->isAbstract() && $kind === 'class') $item['a'] = 1;
        if ($c->isFinal()) $item['f'] = 1;
        $parent = $c->getParentClass();
        if ($parent) $item['parent'] = $parent->getName();
        $ifaces = $c->getInterfaceNames();
        if ($ifaces) $item['i'] = array_values($ifaces);

        $methods = [];
        foreach ($c->getMethods() as $m) {
            if ($m->getDeclaringClass()->getName() !== $c->getName()) continue;
            $mi = ['n' => $m->getName(), 'p' => neo_params($m)];
            if ($m->isStatic()) $mi['s'] = 1;
            if ($m->isProtected()) $mi['v'] = 1;
            if ($m->isPrivate()) $mi['v'] = 2;
            if ($m->isAbstract()) $mi['a'] = 1;
            $r = neo_return($m);
            if ($r !== null) $mi['r'] = $r;
            $methods[] = $mi;
        }
        if ($methods) $item['m'] = $methods;

        $consts = [];
        foreach ($c->getReflectionConstants() as $k) {
            if ($k->getDeclaringClass()->getName() !== $c->getName()) continue;
            $consts[] = $k->getName();
        }
        if ($consts) $item['c'] = $consts;

        $props = [];
        foreach ($c->getProperties() as $p) {
            if ($p->getDeclaringClass()->getName() !== $c->getName()) continue;
            $pi = ['n' => $p->getName()];
            if ($p->isStatic()) $pi['s'] = 1;
            if ($p->isProtected()) $pi['v'] = 1;
            if ($p->isPrivate()) $pi['v'] = 2;
            if ($p->hasType()) $pi['t'] = neo_type($p->getType());
            $props[] = $pi;
        }
        if ($props) $item['pr'] = $props;
        $result['classes'][] = $item;
    } catch (Throwable $e) {}
}

foreach (get_defined_constants(true) as $group => $constants) {
    if ($group === 'user') continue;
    foreach ($constants as $name => $v) $result['constants'][] = $name;
}

echo json_encode($result, JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE);
