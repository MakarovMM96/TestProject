// Cross-check of the SAI2 DPCM encoder used by SAI2 Recovery against the
// reference decoder from libsai (https://github.com/Wunkolo/libsai, MIT).
//
// 1. Encodes random images with the same algorithm as Sai2Recovery.Core
//    (DpcmCodec.EncodeTile) and decodes them with libsai's
//    sai2::ExtractDpcmToBGRA. Every pixel must round-trip exactly.
// 2. Writes test fixtures (synthetic .sai2 files + the pixels libsai decoded
//    from them) that the C# unit tests compare against.
//
// Build: see build.sh in this folder. Asserts must stay enabled (no NDEBUG).

#include <sai2.hpp>

#include <cassert>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <random>
#include <string>
#include <vector>

namespace
{
struct BitWriter
{
	std::vector<std::uint8_t> Bytes;
	std::uint64_t             Acc  = 0;
	int                       Bits = 0;

	void Put(std::uint32_t Value, int Count)
	{
		for( int i = 0; i < Count; ++i )
		{
			Acc |= std::uint64_t((Value >> i) & 1u) << Bits;
			++Bits;
			if( Bits == 8 )
			{
				Bytes.push_back(std::uint8_t(Acc));
				Acc  = 0;
				Bits = 0;
			}
		}
	}
	void PutOpcode(int Op)
	{
		const int f = Op >> 1;
		Put(0, f);
		Put(1, 1);
		Put(Op & 1, 1);
	}
	void Align()
	{
		if( Bits )
		{
			Bytes.push_back(std::uint8_t(Acc));
			Acc  = 0;
			Bits = 0;
		}
	}
};

void EncodeChannel(BitWriter& W, const std::vector<int>& D)
{
	std::size_t i = 0;
	while( i < D.size() )
	{
		if( D[i] == 0 )
		{
			std::size_t Run = 0;
			while( i + Run < D.size() && D[i + Run] == 0 ) ++Run;
			while( Run >= 8 )
			{
				const std::size_t N = Run > 135 ? 135 : Run;
				W.PutOpcode(15);
				W.Put(std::uint32_t(N - 8), 7);
				Run -= N;
				i += N;
			}
			while( Run > 0 )
			{
				W.PutOpcode(0);
				--Run;
				++i;
			}
			continue;
		}
		const int m = D[i] < 0 ? -D[i] : D[i];
		int       n = 0;
		while( (1 << (n + 1)) <= m + 1 ) ++n;
		const int v = (m + 1) - (1 << n);
		W.PutOpcode(n);
		W.Put(std::uint32_t(v), n);
		W.Put(D[i] < 0 ? 1u : 0u, 1);
		++i;
	}
}

int Clamp255(int x) { return x < 0 ? 0 : (x > 255 ? 255 : x); }

// Pixels: BGRA8 (uint32, B in low byte), Stride = image width
std::vector<std::uint8_t> EncodeTile(
	const std::vector<std::uint32_t>& Img, int Stride, int X0, int Y0, int Tw,
	int Th, int Channels, std::uint16_t Header
)
{
	std::vector<std::uint8_t> Out;
	Out.push_back(std::uint8_t(Header));
	Out.push_back(std::uint8_t(Header >> 8));
	std::vector<int> Prev(Tw * 4, 0);
	for( int y = 0; y < Th; ++y )
	{
		BitWriter W;
		std::vector<int> Cur(Tw * 4);
		for( int x = 0; x < Tw; ++x )
		{
			const std::uint32_t P = Img[(Y0 + y) * Stride + X0 + x];
			for( int c = 0; c < 4; ++c ) Cur[x * 4 + c] = (P >> (8 * c)) & 0xFF;
		}
		for( int c = 0; c < Channels; ++c )
		{
			std::vector<int> D(Tw);
			int              Sum = 0;
			for( int x = 0; x < Tw; ++x )
			{
				const int Up     = Prev[x * 4 + c];
				const int UpLeft = x ? Prev[(x - 1) * 4 + c] : 0;
				const int Pred   = Clamp255(Sum + Up - UpLeft);
				D[x]             = Cur[x * 4 + c] - Pred;
				Sum              = Cur[x * 4 + c];
			}
			EncodeChannel(W, D);
		}
		W.Align();
		Out.insert(Out.end(), W.Bytes.begin(), W.Bytes.end());
		// Alpha channel of 3-channel images decodes to 0
		if( Channels == 3 )
			for( int x = 0; x < Tw; ++x ) Cur[x * 4 + 3] = 0;
		Prev = Cur;
	}
	return Out;
}

void Put32(std::vector<std::uint8_t>& V, std::uint32_t X)
{
	for( int i = 0; i < 4; ++i ) V.push_back(std::uint8_t(X >> (8 * i)));
}
void Put16(std::vector<std::uint8_t>& V, std::uint16_t X)
{
	V.push_back(std::uint8_t(X));
	V.push_back(std::uint8_t(X >> 8));
}
void PutTag(std::vector<std::uint8_t>& V, const char* T)
{
	for( int i = 0; i < 4; ++i ) V.push_back(std::uint8_t(T[i]));
}

std::vector<std::uint8_t> EncodeDpcm(
	const std::vector<std::uint32_t>& Img, int W, int H, int Channels
)
{
	const int TilesX = (W + 255) / 256, TilesY = (H + 255) / 256;
	std::vector<std::vector<std::uint8_t>> Tiles;
	std::vector<std::uint8_t>              Body;
	std::vector<std::uint32_t>             Sizes;
	for( int ty = 0; ty < TilesY; ++ty )
	{
		for( int tx = 0; tx < TilesX; ++tx )
		{
			const int Tw = std::min(256, W - tx * 256);
			const int Th = std::min(256, H - ty * 256);
			auto      T  = EncodeTile(
                Img, W, tx * 256, ty * 256, Tw, Th, Channels,
                std::uint16_t((tx << 8) | (ty & 0xFF))
            );
			Sizes.push_back(std::uint32_t(T.size()));
			Body.insert(Body.end(), T.begin(), T.end());
		}
		Put16(Body, std::uint16_t((TilesX << 8) | (ty & 0xFF)));
	}
	std::vector<std::uint8_t> Out;
	PutTag(Out, "dpcm");
	for( auto S : Sizes ) Put32(Out, S);
	Out.insert(Out.end(), Body.begin(), Body.end());
	return Out;
}

std::vector<std::uint32_t> MakeImage(std::mt19937& Rng, int W, int H, int Kind)
{
	std::vector<std::uint32_t> Img(std::size_t(W) * H);
	std::uniform_int_distribution<int> B(0, 255);
	for( int y = 0; y < H; ++y )
		for( int x = 0; x < W; ++x )
		{
			std::uint32_t b, g, r, a;
			switch( Kind % 4 )
			{
			case 0: // noise
				b = B(Rng); g = B(Rng); r = B(Rng); a = B(Rng);
				break;
			case 1: // gradients
				b = (x * 3) & 255; g = (y * 2) & 255; r = (x + y) & 255;
				a = 255;
				break;
			case 2: // flat areas + edges (long zero runs)
				b = (x / 37 + y / 53) % 2 ? 200 : 10; g = 128;
				r = (x > W / 2) ? 255 : 0; a = (y < H / 3) ? 0 : 255;
				break;
			default: // sparse strokes on transparency
				if( (x - W / 2) * (x - W / 2) + (y - H / 2) * (y - H / 2)
					< (W / 4) * (W / 4) )
				{
					b = B(Rng) / 4; g = 40; r = 220; a = 255;
				}
				else
				{
					b = g = r = a = 0;
				}
			}
			Img[std::size_t(y) * W + x] = b | (g << 8) | (r << 16) | (a << 24);
		}
	return Img;
}

sai2::CanvasHeader MakeHeader(int W, int H, int Channels, std::uint32_t Tables)
{
	sai2::CanvasHeader Hd{};
	std::memcpy(Hd.Identifier.data(), "SAI-CANVAS-TYPE0", 16);
	Hd.Flags0                = 0;
	Hd.CanvasBackgroundFlags = Channels == 4 ? 0 : 1;
	Hd.Flags2                = 1;
	Hd.Width                 = W;
	Hd.Height                = H;
	Hd.PrintingResolution    = 350;
	Hd.TableCount            = Tables;
	Hd.SelectedLayer         = 2;
	Hd.CanvasBackgroundColor = 0xFF808080u;
	std::memcpy(&Hd.LayerEffectColor, "norm", 4);
	return Hd;
}

bool RoundTrip(std::mt19937& Rng, int W, int H, int Channels, int Kind)
{
	auto Img = MakeImage(Rng, W, H, Kind);
	auto Blob = EncodeDpcm(Img, W, H, Channels);
	auto Hd   = MakeHeader(W, H, Channels, 1);
	auto [Px, Ow, Oh] = sai2::ExtractDpcmToBGRA(
		Hd, std::span<const std::byte>(
				reinterpret_cast<const std::byte*>(Blob.data()), Blob.size()
			)
	);
	if( Ow != std::uint32_t(W) || Oh != std::uint32_t(H) ) return false;
	const std::uint32_t* Out = reinterpret_cast<const std::uint32_t*>(Px.data());
	for( std::size_t i = 0; i < Img.size(); ++i )
	{
		std::uint32_t Expect = Img[i];
		if( Channels == 3 ) Expect |= 0xFF000000u;
		if( Out[i] != Expect )
		{
			std::printf(
				"Mismatch %dx%d ch%d kind%d at %zu: %08X != %08X\n", W, H,
				Channels, Kind, i, Out[i], Expect
			);
			return false;
		}
	}
	return true;
}

void WriteFile(const std::string& Path, const std::vector<std::uint8_t>& D)
{
	std::ofstream F(Path, std::ios::binary);
	F.write(reinterpret_cast<const char*>(D.data()), D.size());
}

// A synthetic .sai2 file with: hist, intg, layr, lpix (dense) for one layer.
// Layer/history blob layouts follow photopea/SAI2-specification.
void WriteFixture(
	const std::string& Dir, const std::string& Name, std::mt19937& Rng, int W,
	int H, int Channels, int Kind
)
{
	auto Img  = MakeImage(Rng, W, H, Kind);
	auto Intg = EncodeDpcm(Img, W, H, Channels);

	// Layer pixels: 4 channels
	auto LayerImg = MakeImage(Rng, W, H, 3);
	auto Lpix     = EncodeDpcm(LayerImg, W, H, 4);
	const std::uint32_t TileCount
		= std::uint32_t(((W + 255) / 256) * ((H + 255) / 256));

	std::vector<std::uint8_t> Layr;
	PutTag(Layr, "layr");
	Put32(Layr, 2);
	Put32(Layr, 0);
	Put32(Layr, 0);
	PutTag(Layr, "norm");
	Put32(Layr, 0); Put32(Layr, 0); Put32(Layr, W); Put32(Layr, H);
	Put32(Layr, 0);
	Put32(Layr, TileCount);
	PutTag(Layr, "norm");
	Put32(Layr, 100);
	Put32(Layr, 1);
	// name param, UTF-16LE "Слой1"
	const char16_t LayerName[] = u"Слой1";
	PutTag(Layr, "name");
	Put32(Layr, 2 * 5);
	for( int i = 0; i < 5; ++i ) Put16(Layr, std::uint16_t(LayerName[i]));
	Put32(Layr, 0);

	std::vector<std::uint8_t> Hist(32 * 2 + 16 + 4, 0);
	const char16_t HistId[] = u"fixture";
	for( int i = 0; i < 7; ++i )
	{
		Hist[i * 2]     = std::uint8_t(HistId[i]);
		Hist[i * 2 + 1] = std::uint8_t(HistId[i] >> 8);
	}

	struct E { const char* Tag; std::uint32_t Id; const std::vector<std::uint8_t>* D; };
	const E Entries[] = {
		{"hist", 0, &Hist}, {"intg", 0, &Intg}, {"layr", 2, &Layr}, {"lpix", 2, &Lpix}
	};
	const std::uint32_t N = 4;
	auto Hd = MakeHeader(W, H, Channels, N);
	std::vector<std::uint8_t> File(sizeof(Hd));
	std::memcpy(File.data(), &Hd, sizeof(Hd));
	std::uint64_t Off = 64 + 16 * N;
	for( const auto& En : Entries )
	{
		PutTag(File, En.Tag);
		Put32(File, En.Id);
		Put32(File, std::uint32_t(Off));
		Put32(File, std::uint32_t(Off >> 32));
		Off += En.D->size();
	}
	for( const auto& En : Entries ) File.insert(File.end(), En.D->begin(), En.D->end());
	WriteFile(Dir + "/" + Name + ".sai2", File);

	// Decode back with libsai and store expected pixels (BGRA, raw)
	auto [Px, Ow, Oh] = sai2::ExtractDpcmToBGRA(
		Hd, std::span<const std::byte>(
				reinterpret_cast<const std::byte*>(Intg.data()), Intg.size()
			)
	);
	std::vector<std::uint8_t> Raw(
		reinterpret_cast<const std::uint8_t*>(Px.data()),
		reinterpret_cast<const std::uint8_t*>(Px.data()) + Px.size()
	);
	WriteFile(Dir + "/" + Name + ".intg.bgra", Raw);

	// Layer expectation: decode the dense lpix with libsai as a 4-channel image
	auto Hd4 = MakeHeader(W, H, 4, N);
	auto [Lp, Lw, Lh] = sai2::ExtractDpcmToBGRA(
		Hd4, std::span<const std::byte>(
				 reinterpret_cast<const std::byte*>(Lpix.data()), Lpix.size()
			 )
	);
	std::vector<std::uint8_t> LRaw(
		reinterpret_cast<const std::uint8_t*>(Lp.data()),
		reinterpret_cast<const std::uint8_t*>(Lp.data()) + Lp.size()
	);
	WriteFile(Dir + "/" + Name + ".layer2.bgra", LRaw);
	std::printf("fixture %s %dx%d ch%d (%zu bytes)\n", Name.c_str(), W, H, Channels, File.size());
}
} // namespace

int main(int argc, char** argv)
{
	std::mt19937 Rng(12345);
	int          Ok = 0, Total = 0;
	const int    Sizes[][2] = {{1, 1},     {7, 3},     {256, 256}, {257, 255},
							   {300, 520}, {512, 512}, {600, 333}, {1000, 70}};
	for( auto& S : Sizes )
		for( int Ch = 3; Ch <= 4; ++Ch )
			for( int Kind = 0; Kind < 4; ++Kind )
			{
				++Total;
				if( RoundTrip(Rng, S[0], S[1], Ch, Kind) ) ++Ok;
			}
	std::printf("round-trip: %d/%d OK\n", Ok, Total);
	if( argc > 1 )
	{
		const std::string Dir = argv[1];
		WriteFixture(Dir, "small_rgba", Rng, 200, 150, 4, 1);
		WriteFixture(Dir, "multi_tile_rgb", Rng, 600, 300, 3, 2);
	}
	return Ok == Total ? 0 : 1;
}
