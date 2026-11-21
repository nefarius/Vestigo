#include "guid.h"
#include "Provider.h"

#include <atomic>
#include <new>

namespace {
std::atomic<long> g_locks{0};
HINSTANCE g_instance = nullptr;

class ClassFactory final : public IClassFactory
{
public:
    ClassFactory()
    {
        g_locks.fetch_add(1);
    }

    ~ClassFactory()
    {
        g_locks.fetch_sub(1);
    }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr)
        {
            return E_POINTER;
        }
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs_); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const LONG value = InterlockedDecrement(&refs_);
        if (value == 0)
        {
            delete this;
        }
        return static_cast<ULONG>(value);
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (ppv == nullptr)
        {
            return E_POINTER;
        }
        *ppv = nullptr;
        if (outer != nullptr)
        {
            return CLASS_E_NOAGGREGATION;
        }
        auto* provider = new (std::nothrow) VestigoProvider();
        if (provider == nullptr)
        {
            return E_OUTOFMEMORY;
        }
        const HRESULT hr = provider->QueryInterface(riid, ppv);
        provider->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock)
        {
            g_locks.fetch_add(1);
        }
        else
        {
            g_locks.fetch_sub(1);
        }
        return S_OK;
    }

private:
    LONG refs_ = 1;
};

}  // namespace

extern "C" void DllAddRef()
{
    g_locks.fetch_add(1);
}

extern "C" void DllRelease()
{
    g_locks.fetch_sub(1);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_instance = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return g_locks.load() == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (ppv == nullptr)
    {
        return E_POINTER;
    }
    *ppv = nullptr;
    if (clsid != CLSID_VestigoCredentialProvider)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    auto* factory = new (std::nothrow) ClassFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }
    const HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}
