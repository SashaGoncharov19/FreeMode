#include "NativeMemory.hpp"
#include "ScriptDomain.hpp"

#include <Windows.h>

// Reports a game-build specific patch pattern that no longer matches (see NativeMemory.cpp).
static unsigned long long ReportPattern(const char *name, unsigned long long address)
{
	if (address == 0)
	{
		GTA::Native::MemoryAccess::_missingPatterns->Add(gcnew System::String(name));
		GTA::Log("[ERROR]", "Memory pattern not found in this game build: ", gcnew System::String(name));
	}

	return address;
}

// workaround for an unmanaged code
unsigned long long GetOfflinePatchAddr()
{
	return ReportPattern("force offline patch", GTA::Native::MemoryAccess::FindPattern("\x48\x83\x3D\x00\x00\x00\x00\x00\x88\x05\x00\x00\x00\x00\x75\x0B",
		"xxx????xxx????xx"));
}

// Classic hook site: the text lookup that takes the label as a string (game builds up to ~1.0.3500).
// Not reported when missing, HookGameText() falls back to GetLabelTextByHashHookAddr() first.
unsigned long long GetGameTextHookAddr()
{
	return GTA::Native::MemoryAccess::FindPattern("\xE8\x00\x00\x00\x00\x8B\x0D\x8C\x68\xF4\x01\x65\x48\x8B\x04\x25\x58\x00\x00\x00\xBA\xB4\x00\x00\x00\x48\x8B\x04\xC8\x8B\x0C\x02\xD1\xE9\x80\xE1\x01\x0F\xB6\xC1\x48\x8D",
		"x????xx????xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");
}

// True when the address lies inside GTA5.exe (guards the indirect lookups below against a stale pattern).
static bool InGameImage(unsigned long long address)
{
	const auto base = reinterpret_cast<unsigned long long>(GetModuleHandle(nullptr));
	const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER *>(base);
	const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS *>(base + dos->e_lfanew);

	return address >= base && address < base + nt->OptionalHeader.SizeOfImage;
}

// Newer game builds: the string-label lookup above no longer matches, so hook the hash-based lookup
// GetLabelTextByHash(table, hash) instead. It is found the same way upstream ScriptHookVDotNet does it:
// through the call inside DOES_TEXT_LABEL_EXIST (a direct pattern is unreliable because trainers hook the function).
unsigned long long GetLabelTextByHashHookAddr()
{
	unsigned long long address = GTA::Native::MemoryAccess::FindPattern("\x74\x64\x48\x8D\x15\x00\x00\x00\x00\x48\x8D\x0D\x00\x00\x00\x00\xE8\x00\x00\x00\x00\x84\xC0\x74\x33",
		"xxxxx????xxx????x????xxxx");

	if (address != 0)
	{
		// call DoesTextLabelExist at +16, and inside it the call to GetLabelTextByHash at +27
		const unsigned long long doesTextLabelExist = *reinterpret_cast<int *>(address + 17) + address + 21;

		if (InGameImage(doesTextLabelExist) && *reinterpret_cast<unsigned char *>(doesTextLabelExist + 27) == 0xE8)
		{
			const unsigned long long getLabelTextByHash = *reinterpret_cast<int *>(doesTextLabelExist + 28) + doesTextLabelExist + 32;

			if (InGameImage(getLabelTextByHash))
			{
				GTA::Log("[INFO]", "Game text hook: using the text label hash lookup of this game build.");
				return getLabelTextByHash;
			}
		}

		address = 0;
	}

	return ReportPattern("game text hook", address);
}

#pragma unmanaged

#include <Windows.h>
#include <cstdint>

void ForceOffline()
{
	uintptr_t address = GetOfflinePatchAddr();

	if (address)
	{
		address += 8;

		unsigned long dwProtect{};
		unsigned long dwProtect2{};

		VirtualProtect((void*)address, 0x6ui64, 0x40u, &dwProtect);
		memset((void*)address, 0x90, 6);
		VirtualProtect((void*)address, 0x6ui64, dwProtect, &dwProtect2);
	}
}


#include "../../libs/minhook-master/include/MinHook.h"

static const char *const LoadingLabel = "LOADING_SPLAYER_L";
static const char *const LoadingText = "Loading GTA Network";

char *(__fastcall *o_GetGameText)(__int64 a1, BYTE *a2, __int64 a3);

char *__fastcall GetGameText(__int64 a1, BYTE *a2, __int64 a3)
{
	if (strcmp((const char*)a2, LoadingLabel) == 0)
		return (char*)LoadingText;

	return o_GetGameText(a1, a2, a3);
}

// Jenkins one-at-a-time, the hash the game keys text labels with (GET_HASH_KEY lower-cases ASCII first).
static unsigned int Joaat(const char *text, bool lowerCase)
{
	unsigned int hash = 0;

	for (; *text != '\0'; text++)
	{
		unsigned char c = static_cast<unsigned char>(*text);
		if (lowerCase && c >= 'A' && c <= 'Z')
			c += 'a' - 'A';

		hash += c;
		hash += hash << 10;
		hash ^= hash >> 6;
	}

	hash += hash << 3;
	hash ^= hash >> 11;
	hash += hash << 15;

	return hash;
}

static unsigned int LoadingLabelHash = 0, LoadingLabelHashExactCase = 0;

char *(__fastcall *o_GetLabelTextByHash)(__int64 table, unsigned int hash);

char *__fastcall GetLabelTextByHash(__int64 table, unsigned int hash)
{
	if (hash == LoadingLabelHash || hash == LoadingLabelHashExactCase)
		return (char*)LoadingText;

	return o_GetLabelTextByHash(table, hash);
}

void HookGameText()
{
	UINT64 addr = GetGameTextHookAddr();

	if (addr != 0)
	{
		MH_Initialize();

		addr += 0x5 - 0x1A;

		MH_CreateHook((void*)addr, &GetGameText, (void**)&o_GetGameText);
		MH_EnableHook((void*)addr);
		return;
	}

	addr = GetLabelTextByHashHookAddr();

	if (addr != 0)
	{
		LoadingLabelHash = Joaat(LoadingLabel, true);
		LoadingLabelHashExactCase = Joaat(LoadingLabel, false);

		MH_Initialize();

		MH_CreateHook((void*)addr, &GetLabelTextByHash, (void**)&o_GetLabelTextByHash);
		MH_EnableHook((void*)addr);
	}
}