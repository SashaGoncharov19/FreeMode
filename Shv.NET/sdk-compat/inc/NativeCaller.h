// Compatible with the ScriptHookV SDK "NativeCaller.h" (see README.md in the parent folder).
#pragma once

#include "main.h"
// (the SDK version includes types.h through natives.h only; keep Object = System::Object for C++/CLI)

template <typename T>
static inline void nativePush(T val)
{
	UINT64 val64 = 0;
	if (sizeof(T) > sizeof(UINT64))
	{
		throw "error, value size > 64 bit";
	}
	*reinterpret_cast<T *>(&val64) = val;
	nativePush64(val64);
}

static inline void nativePushAll()
{
}

template <typename T, typename... Args>
static inline void nativePushAll(T first, Args... rest)
{
	nativePush(first);
	nativePushAll(rest...);
}

template <typename R, typename... Args>
static inline R invoke(UINT64 hash, Args... args)
{
	nativeInit(hash);
	nativePushAll(args...);
	return *reinterpret_cast<R *>(nativeCall());
}
